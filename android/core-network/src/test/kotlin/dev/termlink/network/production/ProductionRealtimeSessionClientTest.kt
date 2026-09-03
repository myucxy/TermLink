package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.FlowControlPayload
import dev.termlink.protocol.realtime.v1.GapPayload
import dev.termlink.protocol.realtime.v1.InputResultPayload
import dev.termlink.protocol.realtime.v1.LeasePayload
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.realtime.v1.OutputAckPayload
import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.SessionTerminateRequestPayload
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.protocol.realtime.v1.SubscribePayload
import dev.termlink.session.DurableAckCommit
import dev.termlink.session.InputSubmission
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteControlLeaseLostException
import dev.termlink.session.RendererBootstrapState
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionState
import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.receiveAsFlow
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
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import kotlin.test.assertFailsWith
import kotlin.test.assertIs

class ProductionRealtimeSessionClientTest {
    private lateinit var server: MockWebServer
    private lateinit var scope: CoroutineScope
    private val multiplexers = CopyOnWriteArrayList<ProductionRealtimeMultiplexer>()

    @Before
    fun setUp() {
        server = MockWebServer()
        scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    }

    @After
    fun tearDown() {
        multiplexers.forEach(ProductionRealtimeMultiplexer::close)
        multiplexers.clear()
        scope.cancel()
        server.close()
    }

    @Test
    fun sessionUpsertOptionalTitleIsOneOrderedEventAndLegacyLifecycleStillWorks() {
        val titled = SessionUpsertPayload.newBuilder()
            .setLifecycle("CHANGED")
            .setTitle("Codex — 构建")
            .build()
        assertTrue(titled.hasTitle())
        val titleEvent = assertIs<RemoteSessionEvent.Title>(
            ProductionRealtimeSessionClient.sessionUpsertEvent(7, titled),
        )
        assertEquals(7L, titleEvent.sequence)
        assertEquals("Codex — 构建", titleEvent.value)

        val cleared = SessionUpsertPayload.newBuilder()
            .setLifecycle("CHANGED")
            .setTitle("")
            .build()
        assertTrue(cleared.hasTitle())
        assertEquals(
            "",
            assertIs<RemoteSessionEvent.Title>(
                ProductionRealtimeSessionClient.sessionUpsertEvent(8, cleared),
            ).value,
        )

        val legacy = SessionUpsertPayload.newBuilder()
            .setLifecycle("INTERRUPTED")
            .build()
        assertTrue(!legacy.hasTitle())
        val lifecycleEvent = assertIs<RemoteSessionEvent.Lifecycle>(
            ProductionRealtimeSessionClient.sessionUpsertEvent(9, legacy),
        )
        assertEquals(RemoteLifecycle.INTERRUPTED, lifecycleEvent.state)
    }

    @Test
    fun sessionTitlePassThroughPreservesUnpublishedAndExplicitBlankStates(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )
        try {
            assertEquals(null, client.sessionTitle.value)
            client.remoteSession.onConnected()
            client.remoteSession.accept(RemoteSessionEvent.Title(1, ""))
            assertEquals("", client.sessionTitle.value)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    private fun testTransport(
        rootUrl: String,
        androidId: UUID,
        identity: DeviceIdentity,
        sessionId: UUID,
        parentScope: CoroutineScope = scope,
    ): ProductionRealtimeSessionTransport {
        val multiplexer = ProductionRealtimeMultiplexer(
            rootUrl = rootUrl,
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            parentScope = parentScope,
            allowCleartextForLocalTest = true,
        )
        multiplexers += multiplexer
        return multiplexer.register(sessionId)
    }

    private fun Envelope.decryptFlowControl(sessionKey: ByteArray): FlowControlPayload {
        val plaintext = ProductionRealtimeClient.decryptEnvelope(this, sessionKey)
        return try {
            FlowControlPayload.parseFrom(plaintext)
        } finally {
            plaintext.fill(0)
        }
    }

    @Test
    fun subscriberBootstrapPublishesZeroWindowBeforeBoundedOpenWindow(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = GenerationRaceSessionTransport()
        val pairwise = SessionCrypto.derivePairwiseKey(
            privateKey = androidIdentity.exchange.privateKey,
            peerPublicKey = pcIdentity.exchange.publicKey,
            pcDeviceId = pcId,
            androidDeviceId = androidId,
            keyEpoch = 7,
        )
        val sessionKey = SessionCrypto.deriveSessionKey(
            pairwise,
            sessionId,
            CryptoDirection.ANDROID_TO_PC,
            keyId,
        )
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) {
                while (transport.sent.count { it.messageType == MessageType.FLOW_CONTROL } < 2) {
                    kotlinx.coroutines.yield()
                }
            }
            val bootstrap = transport.sent.take(3)
            assertEquals(
                listOf(MessageType.SUBSCRIBE, MessageType.FLOW_CONTROL, MessageType.FLOW_CONTROL),
                bootstrap.map(Envelope::getMessageType),
            )
            val controls = bootstrap.drop(1).map { envelope ->
                val plaintext = ProductionRealtimeClient.decryptEnvelope(envelope, sessionKey)
                try {
                    FlowControlPayload.parseFrom(plaintext)
                } finally {
                    plaintext.fill(0)
                }
            }
            assertTrue(controls[0].paused)
            assertEquals(0, controls[0].maxUnackedEvents)
            assertEquals(0L, controls[0].maxUnackedWireBytes)
            assertFalse(controls[1].paused)
            assertEquals(ProductionRealtimeClient.OPEN_WINDOW_EVENTS, controls[1].maxUnackedEvents)
            assertTrue(ProductionRealtimeClient.OPEN_WINDOW_EVENTS < ProductionRealtimeClient.MAX_BUFFERED_EVENTS)
        } finally {
            client.close()
            sessionKey.fill(0)
            pairwise.fill(0)
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun subscriberBootstrapKeepsZeroWindowWhileReceiveQueueIsPausedThenOpensAfterLowWater(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = GenerationRaceSessionTransport()
        val pairwise = SessionCrypto.derivePairwiseKey(
            privateKey = androidIdentity.exchange.privateKey,
            peerPublicKey = pcIdentity.exchange.publicKey,
            pcDeviceId = pcId,
            androidDeviceId = androidId,
            keyEpoch = 7,
        )
        val sessionKey = SessionCrypto.deriveSessionKey(
            pairwise,
            sessionId,
            CryptoDirection.ANDROID_TO_PC,
            keyId,
        )
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            transport.emitPressure(paused = true)
            client.start()
            withTimeout(2_000) {
                while (transport.sent.count { it.messageType == MessageType.FLOW_CONTROL } < 2) {
                    kotlinx.coroutines.yield()
                }
            }

            val heldControls = transport.sent
                .filter { it.messageType == MessageType.FLOW_CONTROL }
                .take(2)
                .map { it.decryptFlowControl(sessionKey) }
            assertTrue(heldControls.all(FlowControlPayload::getPaused))
            assertTrue(heldControls.all { it.maxUnackedEvents == 0 })
            assertEquals("ANDROID_SUBSCRIBER_BOOTSTRAP", heldControls[0].reasonCode)
            assertEquals("ANDROID_RENDER_QUEUE_HIGH_WATER", heldControls[1].reasonCode)

            transport.emitPressure(paused = false)
            withTimeout(2_000) {
                while (transport.sent.count { it.messageType == MessageType.FLOW_CONTROL } < 3) {
                    kotlinx.coroutines.yield()
                }
            }
            val opened = transport.sent
                .filter { it.messageType == MessageType.FLOW_CONTROL }
                .last()
                .decryptFlowControl(sessionKey)
            assertFalse(opened.paused)
            assertEquals(ProductionRealtimeClient.OPEN_WINDOW_EVENTS, opened.maxUnackedEvents)
            assertEquals(ProductionRealtimeClient.OPEN_WINDOW_WIRE_BYTES, opened.maxUnackedWireBytes)
        } finally {
            client.close()
            sessionKey.fill(0)
            pairwise.fill(0)
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun generationReplacementWhileOpenWindowSendIsBlockedCannotOpenRetiredGeneration(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        val blockedOpen = transport.blockNthGenerationSend(MessageType.FLOW_CONTROL, occurrence = 2)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            assertTrue(blockedOpen.entered.await(2, TimeUnit.SECONDS))
            val generationOne = transport.currentConnectedGeneration()
            assertEquals(
                listOf(generationOne),
                transport.generationBoundSends
                    .filter { it.first == MessageType.FLOW_CONTROL }
                    .map { it.second },
            )

            val generationTwo = transport.replaceGeneration()
            blockedOpen.release.countDown()
            transport.emitConnectedEvent(generationTwo)
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady ||
                    transport.generationBoundSends.count {
                        it.first == MessageType.FLOW_CONTROL && it.second == generationTwo
                    } < 2
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            assertEquals(
                listOf(generationOne, generationTwo, generationTwo),
                transport.generationBoundSends
                    .filter { it.first == MessageType.FLOW_CONTROL }
                    .map { it.second },
            )
            assertEquals(
                listOf(generationOne, generationTwo),
                transport.generationBoundSends
                    .filter { it.first == MessageType.SUBSCRIBE }
                    .map { it.second },
            )
            assertTrue(transport.disconnectReasons.isEmpty())
        } finally {
            blockedOpen.release.countDown()
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun fatalFrameRetiresOnlySessionEndpointAndFencesQueuedSuccessor(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = QueuedSessionTransport()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val failures = CopyOnWriteArrayList<ProductionRendererFatalFailure>()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(outputs),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            initialRendererState = RendererBootstrapState.FullPrefix(
                sequence = 305,
                rendererGeneration = 1,
            ),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            rendererFatalObserver = failures::add,
        )
        val replayOne = incomingOutputEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 306, byteArrayOf(6),
        )
        val replayTwo = incomingOutputEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 307, byteArrayOf(7),
        )
        val corruptedCiphertext = replayOne.ciphertext.toByteArray().also {
            it[it.lastIndex] = (it.last().toInt() xor 1).toByte()
        }
        val corruptOne = replayOne.toBuilder()
            .setCiphertext(ByteString.copyFrom(corruptedCiphertext))
            .build()
        corruptedCiphertext.fill(0)

        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }

            // Both frames are queued before the collector can complete the
            // fatal decrypt. Sequence 2 must never reach RemoteTerminalSession.
            transport.deliver(corruptOne, generation = 1)
            transport.deliver(replayTwo, generation = 1)
            withTimeout(2_000) {
                while (failures.isEmpty()) kotlinx.coroutines.yield()
            }
            withTimeout(2_000) {
                while (transport.releasedReservations < 2) kotlinx.coroutines.yield()
            }
            withTimeout(2_000) {
                while (transport.gracefulCloseCount == 0) kotlinx.coroutines.yield()
            }
            assertEquals(306L, failures.single().sequence)
            assertEquals(MessageType.OUTPUT_CHUNK, failures.single().messageType)
            assertTrue(outputs.isEmpty())
            assertTrue(client.sessionState.value !is RemoteSessionState.HistoryGap)
            assertIs<ProductionTransportState.Failed>(client.transportState.value)
            assertFalse(client.diagnostics().reconnectActive)
            assertTrue(transport.disconnectReasons.isEmpty())
            assertEquals(0L, client.diagnostics().lastHandledSequence)

            // A session-incarnation failure must not reconnect the poisoned
            // renderer or replace the physical socket shared by siblings.
            transport.emitConnected()
            delay(50)
            assertEquals(1, client.diagnostics().subscribeSendCount)
            assertFalse(client.diagnostics().subscriberReady)
        } finally {
            client.close()
            keyId.fill(0)
            outputs.forEach { it.fill(0) }
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun shortLivedConnectionsKeepIncreasingReconnectBackoffUntilStable(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = QueuedSessionTransport()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(10, 20, 40),
            reconnectStabilityMillis = 500,
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) { client.transportState.first { it is ProductionTransportState.Connected } }
            assertEquals(1, transport.connectCount)

            transport.emitFailed("first short connection")
            withTimeout(2_000) {
                client.transportState.first {
                    it is ProductionTransportState.Reconnecting && it.attempt == 1
                }
            }
            withTimeout(2_000) {
                while (transport.connectCount < 2) kotlinx.coroutines.yield()
            }
            withTimeout(2_000) { client.transportState.first { it is ProductionTransportState.Connected } }

            transport.emitFailed("second short connection")
            withTimeout(2_000) {
                client.transportState.first {
                    it is ProductionTransportState.Reconnecting && it.attempt == 2
                }
            }
            withTimeout(2_000) {
                while (transport.connectCount < 3) kotlinx.coroutines.yield()
            }
            withTimeout(2_000) { client.transportState.first { it is ProductionTransportState.Connected } }
            assertEquals(2, client.diagnostics().reconnectAttempt)

            withTimeout(2_000) {
                while (client.diagnostics().reconnectAttempt != 0) delay(10)
            }
            assertEquals(0, client.diagnostics().reconnectAttempt)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun staleConnectedCannotSubscribeOrDisconnectReplacementGeneration(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) {
                while (transport.subscribeCount != 1) kotlinx.coroutines.yield()
            }

            // First cover replacement after the old Connected handler has
            // validated G1 but before its SUBSCRIBE reaches the socket.
            val blocked = transport.blockNextSubscribe()
            val generationOne = transport.currentConnectedGeneration()
            transport.emitConnectedEvent(generationOne)
            assertTrue(blocked.entered.await(2, TimeUnit.SECONDS))
            val generationTwo = transport.replaceGeneration()
            blocked.release.countDown()
            transport.emitConnectedEvent(generationTwo)
            withTimeout(2_000) {
                while (transport.subscribeCount != 2) kotlinx.coroutines.yield()
            }
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())

            // Then cover a G2 Connected event that was queued before replacement
            // but is not consumed until G3 already owns the physical socket.
            val readsBeforeStaleEvent = transport.connectedGenerationReads
            val generationThree = transport.replaceGeneration()
            transport.emitConnectedEvent(generationTwo)
            withTimeout(2_000) {
                while (transport.connectedGenerationReads == readsBeforeStaleEvent) {
                    kotlinx.coroutines.yield()
                }
            }
            assertEquals(2, transport.subscribeCount)
            assertTrue(transport.disconnectReasons.isEmpty())

            transport.emitConnectedEvent(generationThree)
            withTimeout(2_000) {
                while (transport.subscribeCount != 3) kotlinx.coroutines.yield()
            }
            assertIs<ProductionTransportState.Connected>(client.transportState.value)
            assertTrue(transport.disconnectReasons.isEmpty())
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun replacementAfterSubscribeFencesOldFlowControlAndPreservesSuccessorReadiness(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        val blockedFlowControl = transport.blockNextGenerationSend(MessageType.FLOW_CONTROL)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            assertTrue(blockedFlowControl.entered.await(2, TimeUnit.SECONDS))
            assertEquals(1, transport.subscribeCount)
            val generationOne = transport.currentConnectedGeneration()

            val generationTwo = transport.replaceGeneration()
            transport.emitConnectedEvent(generationTwo)
            blockedFlowControl.release.countDown()

            withTimeout(2_000) {
                while (transport.subscribeCount != 2 || !client.diagnostics().subscriberReady) {
                    kotlinx.coroutines.yield()
                }
            }
            withTimeout(2_000) {
                while (transport.generationBoundSends.none {
                        it.first == MessageType.FLOW_CONTROL && it.second == generationTwo
                    }
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            assertEquals(
                listOf(generationTwo, generationTwo),
                transport.generationBoundSends
                    .filter { it.first == MessageType.FLOW_CONTROL }
                    .map { it.second },
            )
            assertTrue(generationTwo > generationOne)
            assertTrue(client.diagnostics().subscriberReady)
            assertTrue(transport.disconnectReasons.isEmpty())
        } finally {
            blockedFlowControl.release.countDown()
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun sameGenerationSubscribeFailureReconcilesWithoutReplacingPhysicalSocket(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        transport.failNextGenerationSend(MessageType.SUBSCRIBE)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(25),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Reconnecting }
            }
            assertFalse(client.diagnostics().subscriberReady)

            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady ||
                    transport.generationBoundSends.none { it.first == MessageType.FLOW_CONTROL }
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            val generation = transport.currentConnectedGeneration()
            assertEquals(
                listOf(generation, generation),
                transport.generationSendAttempts
                    .filter { it.first == MessageType.SUBSCRIBE }
                    .map { it.second },
            )
            assertEquals(1, transport.connectCount)
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Connected }
            }
            assertIs<ProductionTransportState.Connected>(client.transportState.value)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun sameGenerationFlowControlFailureRestoresDesiredControlWithoutReplacingSocket(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val firstLeaseId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
        val restoredLeaseId = UUID.fromString("11234567-89ab-cdef-0123-456789abcdef")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """{"lease_id":"$firstLeaseId","expires_at":"2099-01-01T00:00:00Z","version":1}""",
                ),
        )
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """{"lease_id":"$restoredLeaseId","expires_at":"2099-01-01T00:00:00Z","version":2}""",
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
            transport = transport,
            reconnectDelaysMillis = listOf(25),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )

        try {
            client.start()
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady) kotlinx.coroutines.yield()
            }
            client.requestControl()
            assertEquals(
                firstLeaseId.toString(),
                assertIs<RemoteSessionState.Controlling>(client.sessionState.value).leaseId,
            )

            transport.failNextGenerationSend(MessageType.FLOW_CONTROL)
            transport.emitPressure(paused = true)
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Reconnecting }
            }
            assertFalse(client.diagnostics().subscriberReady)

            val restored = withTimeout(2_000) {
                client.sessionState.first {
                    it is RemoteSessionState.Controlling && it.leaseId == restoredLeaseId.toString()
                }
            }
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady ||
                    transport.generationSendAttempts.count { it.first == MessageType.FLOW_CONTROL } < 3
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            assertEquals(2L, assertIs<RemoteSessionState.Controlling>(restored).leaseVersion)
            assertEquals(1, transport.connectCount)
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())
            assertEquals("/v1/sessions/$sessionId/lease", server.takeRequest(2, TimeUnit.SECONDS)?.path)
            assertEquals("/v1/sessions/$sessionId/lease", server.takeRequest(2, TimeUnit.SECONDS)?.path)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun generationChangeCancelsStaleSameGenerationReconcile(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        transport.failNextGenerationSend(MessageType.SUBSCRIBE)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(250),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Reconnecting }
            }
            val generationOne = transport.currentConnectedGeneration()
            val generationTwo = transport.replaceGeneration()
            transport.emitConnectedEvent(generationTwo)
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady ||
                    transport.generationBoundSends.none {
                        it.first == MessageType.FLOW_CONTROL && it.second == generationTwo
                    }
                ) {
                    kotlinx.coroutines.yield()
                }
            }
            delay(300)

            assertEquals(
                listOf(generationOne, generationTwo),
                transport.generationSendAttempts
                    .filter { it.first == MessageType.SUBSCRIBE }
                    .map { it.second },
            )
            assertEquals(1, transport.connectCount)
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun closeCancelsPendingSameGenerationReconcile(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = GenerationRaceSessionTransport()
        transport.failNextGenerationSend(MessageType.SUBSCRIBE)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(250),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Reconnecting }
            }
            val sendsBeforeClose = transport.generationSendAttempts.size
            client.close()
            delay(300)

            assertEquals(sendsBeforeClose, transport.generationSendAttempts.size)
            assertEquals(1, transport.connectCount)
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())
            assertFalse(client.diagnostics().reconnectActive)
            assertIs<ProductionTransportState.Closed>(client.transportState.value)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun blockedRendererLaneDoesNotDelaySameGenerationSessionReconcile(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = GenerationRaceSessionTransport()
        val feedStarted = CountDownLatch(1)
        val releaseFeed = CountDownLatch(1)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(
                outputs = CopyOnWriteArrayList(),
                onFeed = { sequence, _ ->
                    if (sequence == 1L) {
                        feedStarted.countDown()
                        check(releaseFeed.await(2, TimeUnit.SECONDS)) {
                            "timed out releasing blocked live feed"
                        }
                    }
                },
            ),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(0),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )
        val output = incomingOutputEnvelope(
            sessionId,
            pcId,
            androidId,
            pcIdentity,
            androidIdentity,
            keyId,
            1,
            byteArrayOf(1),
        )

        try {
            client.start()
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady) kotlinx.coroutines.yield()
            }
            val generation = transport.currentConnectedGeneration()
            transport.deliver(output, generation)
            assertTrue(withContext(Dispatchers.IO) { feedStarted.await(2, TimeUnit.SECONDS) })

            transport.failNextGenerationSend(MessageType.FLOW_CONTROL)
            transport.emitPressure(paused = true)
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady ||
                    transport.generationSendAttempts.count { it.first == MessageType.SUBSCRIBE } < 2 ||
                    transport.generationBoundSends.count { it.first == MessageType.FLOW_CONTROL } < 4 ||
                    client.transportState.value !is ProductionTransportState.Connected
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            assertEquals(1L, releaseFeed.count)
            assertEquals(1, transport.connectCount)
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())
            assertIs<ProductionTransportState.Connected>(client.transportState.value)
        } finally {
            releaseFeed.countDown()
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun externallyOwnedReconnectCancelsPendingBackoffWithoutResettingAckOrConnectingAgain(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = QueuedSessionTransport()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            initialRendererState = RendererBootstrapState.FullPrefix(25, 3),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) { client.transportState.first { it is ProductionTransportState.Connected } }
            assertEquals(25L, client.sessionState.value.lastAckedSequence)
            assertEquals(1, transport.connectCount)

            transport.emitFailed("offline")
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Reconnecting && it.attempt == 1 }
            }
            assertTrue(client.diagnostics().reconnectActive)

            transport.emitState(
                RealtimeState.Reconnecting(
                    ProductionRealtimeMultiplexer.EXTERNAL_RECONNECT_ATTEMPT,
                    "foreground reconcile",
                ),
            )
            withTimeout(2_000) {
                while (client.diagnostics().reconnectActive) kotlinx.coroutines.yield()
            }
            transport.emitState(RealtimeState.Connecting)
            delay(50)

            assertEquals(1, transport.connectCount)
            assertEquals(25L, client.sessionState.value.lastAckedSequence)
            assertEquals(25L, client.diagnostics().lastSubscribeCursor)

            transport.emitConnected()
            withTimeout(2_000) { client.transportState.first { it is ProductionTransportState.Connected } }
            assertEquals(25L, client.sessionState.value.lastAckedSequence)
            assertEquals(1, transport.connectCount)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun gracefulCloseSendsEncryptedUnsubscribeBeforeEndpointRemoval(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = QueuedSessionTransport()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )

        try {
            client.start()
            withTimeout(2_000) { client.transportState.first { it is ProductionTransportState.Connected } }

            client.closeGracefully()

            val unsubscribe = transport.sent.last()
            assertEquals(MessageType.UNSUBSCRIBE, unsubscribe.messageType)
            assertTrue(unsubscribe.ciphertext.size() >= 16)
            assertEquals(1, transport.gracefulCloseCount)
            assertEquals(0, transport.immediateCloseCount)
            assertIs<ProductionTransportState.Closed>(client.transportState.value)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun rendererFailureFencesTheClientAndNeverReconnectsThePoisonedEngine(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = QueuedSessionTransport()
        val failures = CopyOnWriteArrayList<ProductionRendererFatalFailure>()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList(), failOnFeedSequence = 306),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(0),
            initialRendererState = RendererBootstrapState.FullPrefix(305, 1),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            rendererFatalObserver = failures::add,
        )
        val failed = incomingOutputEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 306, byteArrayOf(6),
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }
            transport.deliver(failed, generation = 1)
            withTimeout(2_000) {
                while (failures.isEmpty()) kotlinx.coroutines.yield()
            }

            assertEquals(306L, failures.single().sequence)
            assertEquals(MessageType.OUTPUT_CHUNK, failures.single().messageType)
            assertEquals(1, client.diagnostics().subscribeSendCount)
            assertTrue(client.diagnostics().reconnectActive.not())
            assertTrue(transport.disconnectReasons.isEmpty())
            withTimeout(2_000) {
                while (transport.gracefulCloseCount == 0) kotlinx.coroutines.yield()
            }

            transport.emitConnected()
            delay(50)
            assertEquals(1, client.diagnostics().subscribeSendCount)
            assertTrue(client.diagnostics().subscriberReady.not())
        } finally {
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun pcHeartbeatIsFatalSemanticAndFencesSameGenerationSuccessor(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = QueuedSessionTransport()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val failures = CopyOnWriteArrayList<ProductionRendererFatalFailure>()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(outputs),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            initialRendererState = RendererBootstrapState.FullPrefix(305, 1),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            rendererFatalObserver = failures::add,
        )
        val heartbeat = incomingHeartbeatEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 306,
        )
        val successor = incomingOutputEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 307, byteArrayOf(7),
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }
            transport.deliver(heartbeat, generation = 1)
            transport.deliver(successor, generation = 1)

            withTimeout(2_000) {
                while (failures.isEmpty()) kotlinx.coroutines.yield()
            }
            withTimeout(2_000) {
                while (transport.releasedReservations < 2) kotlinx.coroutines.yield()
            }
            withTimeout(2_000) {
                while (transport.gracefulCloseCount == 0) kotlinx.coroutines.yield()
            }
            assertEquals(306L, failures.single().sequence)
            assertEquals(MessageType.HEARTBEAT, failures.single().messageType)
            assertTrue(outputs.isEmpty())
            assertTrue(client.sessionState.value !is RemoteSessionState.HistoryGap)
            assertIs<ProductionTransportState.Failed>(client.transportState.value)
            assertFalse(client.diagnostics().reconnectActive)
            assertTrue(transport.disconnectReasons.isEmpty())
            assertEquals(0L, client.diagnostics().lastHandledSequence)

            transport.emitConnected()
            delay(50)
            assertEquals(1, client.diagnostics().subscribeSendCount)
            assertFalse(client.diagnostics().subscriberReady)
        } finally {
            client.close()
            keyId.fill(0)
            outputs.forEach { it.fill(0) }
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun oldGenerationSuccessorCannotCrossNewGenerationFence(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = QueuedSessionTransport()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(outputs),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )
        val first = incomingOutputEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 1, byteArrayOf(1),
        )
        val second = incomingOutputEnvelope(
            sessionId, pcId, androidId, pcIdentity, androidIdentity, keyId, 2, byteArrayOf(2),
        )
        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }

            transport.deliver(first, generation = 2)
            transport.deliver(second, generation = 1)
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 1L } }
            withTimeout(2_000) {
                while (transport.releasedReservations < 2) kotlinx.coroutines.yield()
            }
            assertEquals(listOf(1), outputs.map { it.single().toInt() })
            assertTrue(client.sessionState.value !is RemoteSessionState.HistoryGap)

            transport.deliver(second, generation = 2)
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 2L } }
            assertEquals(listOf(1, 2), outputs.map { it.single().toInt() })
        } finally {
            client.close()
            keyId.fill(0)
            outputs.forEach { it.fill(0) }
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun terminationWaitsForSubscriptionAndSendsEncryptedManageRequest(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val received = CompletableDeferred<Pair<Envelope, SessionTerminateRequestPayload>>()
        val helloReceived = CompletableDeferred<WebSocket>()
        val serverWebSocket = AtomicReference<WebSocket>()
        val serverWebSocketClosed = CountDownLatch(1)

        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: okhttp3.Response) {
                        serverWebSocket.set(webSocket)
                    }

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        helloReceived.complete(webSocket)
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
                            if (envelope.messageType == MessageType.SESSION_TERMINATE_REQUEST) {
                                received.complete(
                                    envelope to SessionTerminateRequestPayload.parseFrom(plaintext),
                                )
                            }
                        } finally {
                            plaintext.fill(0)
                            sessionKey.fill(0)
                            pairwise.fill(0)
                        }
                    }

                    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                        serverWebSocketClosed.countDown()
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            val pendingRequest = async { client.requestTermination() }
            val webSocket = withTimeout(5_000) { helloReceived.await() }
            assertTrue(!pendingRequest.isCompleted)
            check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
            val requestId = withTimeout(5_000) { pendingRequest.await() }
            val (envelope, payload) = withTimeout(5_000) { received.await() }

            assertEquals(MessageType.SESSION_TERMINATE_REQUEST, envelope.messageType)
            assertEquals(83, envelope.messageTypeValue)
            assertEquals(Direction.ANDROID_TO_PC, envelope.direction)
            assertArrayEquals(ProductionRealtimeClient.uuidBytes(pcId), envelope.recipientDeviceId.toByteArray())
            assertArrayEquals(ProductionRealtimeClient.uuidBytes(requestId), payload.requestId.toByteArray())
            assertEquals(16, payload.requestId.size())
            assertEquals("USER_REQUESTED", payload.reasonCode)
        } finally {
            if (serverWebSocket.getAndSet(null)?.close(1000, "test complete") == true) {
                serverWebSocketClosed.await(2, TimeUnit.SECONDS)
            }
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
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
        val persistedAcknowledgements = CopyOnWriteArrayList<DurableAckCommit>()
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            reconnectDelaysMillis = listOf(25),
            acknowledgementObserver = { generation, sequence ->
                DurableAckCommit(sequence, generation).also(persistedAcknowledgements::add)
            },
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(10_000) { client.sessionState.first { it.lastAckedSequence == 4L } }
            assertEquals(listOf(0L, 1L), resumes)
            assertDurableCursor(persistedAcknowledgements, expectedFinal = 4L, expectedGeneration = 1L)
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            subscriberRefreshIntervalMillis = 25,
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
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
        val durableAcks = CopyOnWriteArrayList<DurableAckCommit>()
        val wireAcks = CopyOnWriteArrayList<Long>()
        val wireOrder = CopyOnWriteArrayList<String>()
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
                                    wireOrder += "SUBSCRIBE:$resume"
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
                                    wireOrder += "ACK:$ack"
                                }
                                MessageType.FLOW_CONTROL -> {
                                    val flow = FlowControlPayload.parseFrom(plaintext)
                                    wireOrder += "FLOW:${flow.paused}:${flow.maxUnackedEvents}:${flow.reasonCode}"
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = { generation, sequence ->
                DurableAckCommit(sequence, generation).also(durableAcks::add)
            },
            acknowledgementResetObserver = ::resetAcknowledgement,
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
                while (true) {
                    val recoveryIndex = wireOrder.indexOf("SUBSCRIBE:3")
                    val openAfterRecovery = recoveryIndex >= 0 &&
                        wireOrder.drop(recoveryIndex + 1).any {
                            it == "FLOW:false:${ProductionRealtimeClient.OPEN_WINDOW_EVENTS}:" +
                                "ANDROID_RENDER_QUEUE_AVAILABLE"
                        }
                    if (resumes.lastOrNull() == 3L && wireAcks.lastOrNull() == 3L && openAfterRecovery) break
                    kotlinx.coroutines.yield()
                }
            }
            assertEquals(listOf(0L, 3L), resumes.take(2))
            val recoverySubscribeIndex = wireOrder.indexOf("SUBSCRIBE:3")
            assertTrue(recoverySubscribeIndex >= 0)
            val recoveryControls = wireOrder.drop(recoverySubscribeIndex + 1)
                .filter { it.startsWith("FLOW:") }
            assertTrue(recoveryControls.isNotEmpty())
            assertTrue(recoveryControls.first().startsWith("FLOW:true:0:"))
            val recoveryOpenIndex = recoveryControls.indexOfFirst {
                it == "FLOW:false:${ProductionRealtimeClient.OPEN_WINDOW_EVENTS}:" +
                    "ANDROID_RENDER_QUEUE_AVAILABLE"
            }
            assertTrue(recoveryOpenIndex > 0)
            assertTrue(
                recoveryControls.take(recoveryOpenIndex).contains(
                    "FLOW:true:0:ANDROID_SUBSCRIBER_BOOTSTRAP",
                ),
            )
            assertEquals(
                "FLOW:false:${ProductionRealtimeClient.OPEN_WINDOW_EVENTS}:ANDROID_RENDER_QUEUE_AVAILABLE",
                recoveryControls[recoveryOpenIndex],
            )
            assertTrue(recoveryControls.take(recoveryOpenIndex).all { it.startsWith("FLOW:true:0:") })
            assertDurableCursor(durableAcks, expectedFinal = 3L, expectedGeneration = 1L)
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
    fun recoverFromHistoryFeedFailureResetsRendererWithoutRegressingAckAndCanRetry(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val resumes = CopyOnWriteArrayList<Long>()
        val durableAcks = CopyOnWriteArrayList<DurableAckCommit>()
        val durableResets = CopyOnWriteArrayList<DurableAckCommit>()
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = { generation, sequence ->
                DurableAckCommit(sequence, generation).also(durableAcks::add)
            },
            acknowledgementResetObserver = { generation ->
                resetAcknowledgement(generation).also(durableResets::add)
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
            assertTrue(durableResets.isEmpty())

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
            assertDurableCursor(durableAcks, expectedFinal = 3L, expectedGeneration = 1L)
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
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
    fun mainLikeParentNeverRunsOrderedRendererWorkAndReplayRemainsSerial(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = QueuedSessionTransport()
        val outputs = CopyOnWriteArrayList<ByteArray>()
        val feedObservations = CopyOnWriteArrayList<Pair<Long, Thread>>()
        val replayMidpoint = CountDownLatch(1)
        val releaseReplay = CountDownLatch(1)
        val mainLikeExecutor = Executors.newSingleThreadExecutor { runnable ->
            Thread(runnable, "TermLink-test-main").apply { isDaemon = true }
        }
        val mainLikeDispatcher = mainLikeExecutor.asCoroutineDispatcher()
        val mainLikeScope = CoroutineScope(SupervisorJob() + mainLikeDispatcher)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(
                outputs = outputs,
                onFeed = { sequence, thread ->
                    feedObservations += sequence to thread
                    if (sequence == 2_048L) {
                        replayMidpoint.countDown()
                        check(releaseReplay.await(2, TimeUnit.SECONDS)) {
                            "timed out releasing the long-history replay"
                        }
                    }
                },
            ),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
                keyId = keyId.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = mainLikeScope,
            transport = transport,
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )
        val incoming = (1L..12L).map { sequence ->
            incomingOutputEnvelope(
                sessionId,
                pcId,
                androidId,
                pcIdentity,
                androidIdentity,
                keyId,
                sequence,
                byteArrayOf(sequence.toByte()),
            )
        }
        try {
            withContext(mainLikeDispatcher) { client.start() }
            withTimeout(2_000) {
                client.transportState.first { it is ProductionTransportState.Connected }
            }
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }

            val deliveryThread = withContext(mainLikeDispatcher) {
                Thread.currentThread().also { caller ->
                    incoming.forEach { envelope -> transport.deliver(envelope, generation = 1) }
                    assertTrue(caller.name.startsWith("TermLink-test-main"))
                }
            }
            withTimeout(2_000) { client.sessionState.first { it.lastAckedSequence == 12L } }
            assertEquals((1L..12L).toList(), feedObservations.map { it.first })
            assertTrue(feedObservations.none { it.second === deliveryThread })

            // Rebuild a verified prefix from the same Main-like caller. The
            // public suspend API must switch before clear/feed/JNI work begins.
            withContext(mainLikeDispatcher) {
                client.remoteSession.accept(RemoteSessionEvent.Gap(4_097, 13, 4_096))
            }
            val gap = assertIs<RemoteSessionState.HistoryGap>(client.sessionState.value)
            feedObservations.clear()
            val replay = (1L..4_096L).map { sequence ->
                RemoteSessionEvent.Output(sequence, byteArrayOf(sequence.toByte()))
            }
            try {
                val recovery = mainLikeScope.async {
                    client.recoverFromHistory(replay, expectedGap = gap)
                }
                assertTrue(withContext(Dispatchers.IO) { replayMidpoint.await(2, TimeUnit.SECONDS) })
                val heartbeatThread = withTimeout(2_000) {
                    mainLikeScope.async { Thread.currentThread() }.await()
                }
                assertTrue(heartbeatThread.name.startsWith("TermLink-test-main"))
                assertFalse(recovery.isCompleted)
                releaseReplay.countDown()

                assertEquals(4_096L, withTimeout(5_000) { recovery.await() })
                assertEquals((1L..4_096L).toList(), feedObservations.map { it.first })
                assertTrue(feedObservations.none { it.second === deliveryThread })
            } finally {
                releaseReplay.countDown()
                replay.forEach { (it as RemoteSessionEvent.Output).bytes.fill(0) }
            }

            withContext(mainLikeDispatcher) { client.close() }
            assertIs<ProductionTransportState.Closed>(client.transportState.value)
        } finally {
            client.close()
            mainLikeScope.cancel()
            mainLikeDispatcher.close()
            keyId.fill(0)
            outputs.forEach { it.fill(0) }
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun closeCancelsBlockedHistoryRecoveryBeforeFurtherFeedOrPublication(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = QueuedSessionTransport()
        val feedStarted = CountDownLatch(1)
        val releaseFeed = CountDownLatch(1)
        val feedSequences = CopyOnWriteArrayList<Long>()
        val disposed = CompletableDeferred<Unit>()
        val durablePublishes = AtomicInteger(0)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(
                outputs = CopyOnWriteArrayList(),
                onDispose = { disposed.complete(Unit) },
                onFeed = { sequence, _ ->
                    feedSequences += sequence
                    if (sequence == 2L) {
                        feedStarted.countDown()
                        check(releaseFeed.await(2, TimeUnit.SECONDS)) {
                            "timed out releasing blocked history feed"
                        }
                    }
                },
            ),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            acknowledgementObserver = { generation, sequence ->
                if (sequence == 4L) durablePublishes.incrementAndGet()
                DurableAckCommit(sequence, generation)
            },
            acknowledgementResetObserver = ::resetAcknowledgement,
        )
        val replay = (1L..4L).map { sequence ->
            RemoteSessionEvent.Output(sequence, byteArrayOf(sequence.toByte()))
        }

        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady) kotlinx.coroutines.yield()
            }
            client.remoteSession.accept(RemoteSessionEvent.Gap(5, 1, 4))
            val gap = assertIs<RemoteSessionState.HistoryGap>(client.sessionState.value)
            val sentBeforeRecovery = transport.sent.size
            val durableBeforeRecovery = durablePublishes.get()
            val recovery = scope.async { client.recoverFromHistory(replay, expectedGap = gap) }
            assertTrue(withContext(Dispatchers.IO) { feedStarted.await(2, TimeUnit.SECONDS) })

            client.close()
            releaseFeed.countDown()

            val failure = runCatching { withTimeout(2_000) { recovery.await() } }.exceptionOrNull()
            assertTrue(
                "close returned an unrelated recovery failure: $failure",
                failure is CancellationException ||
                    (failure is IllegalStateException && failure.message?.contains("closed") == true),
            )
            withTimeout(2_000) { disposed.await() }
            assertEquals(listOf(1L, 2L), feedSequences)
            assertEquals(durableBeforeRecovery, durablePublishes.get())
            assertEquals(sentBeforeRecovery, transport.sent.size)
            assertIs<ProductionTransportState.Closed>(client.transportState.value)
        } finally {
            releaseFeed.countDown()
            client.close()
            replay.forEach { (it as RemoteSessionEvent.Output).bytes.fill(0) }
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun blockedRendererLaneDoesNotDelaySuccessorGenerationBootstrap(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = GenerationRaceSessionTransport()
        val feedStarted = CountDownLatch(1)
        val releaseFeed = CountDownLatch(1)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(
                outputs = CopyOnWriteArrayList(),
                onFeed = { sequence, _ ->
                    if (sequence == 1L) {
                        feedStarted.countDown()
                        check(releaseFeed.await(2, TimeUnit.SECONDS)) {
                            "timed out releasing blocked successor-generation feed"
                        }
                    }
                },
            ),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(30_000),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )
        val output = incomingOutputEnvelope(
            sessionId,
            pcId,
            androidId,
            pcIdentity,
            androidIdentity,
            keyId,
            1,
            byteArrayOf(1),
        )

        try {
            client.start()
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady) kotlinx.coroutines.yield()
            }
            val generationOne = transport.currentConnectedGeneration()
            transport.deliver(output, generationOne)
            assertTrue(withContext(Dispatchers.IO) { feedStarted.await(2, TimeUnit.SECONDS) })

            val generationTwo = transport.replaceGeneration()
            transport.emitConnectedEvent(generationTwo)
            withTimeout(2_000) {
                while (!client.diagnostics().subscriberReady ||
                    transport.generationBoundSends.none {
                        it.first == MessageType.SUBSCRIBE && it.second == generationTwo
                    } ||
                    transport.generationBoundSends.none {
                        it.first == MessageType.FLOW_CONTROL && it.second == generationTwo
                    }
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            assertEquals(1L, releaseFeed.count)
            assertTrue(generationTwo > generationOne)
            assertEquals(1, transport.connectCount)
            assertEquals(0, transport.conditionalDisconnectAttempts)
            assertTrue(transport.disconnectReasons.isEmpty())
            assertEquals(generationTwo, transport.currentConnectedGeneration())
            assertIs<ProductionTransportState.Connected>(client.transportState.value)
        } finally {
            releaseFeed.countDown()
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun blockedRendererLaneDoesNotDelayControlLaneReconnect(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val keyId = "epoch-00000007".encodeToByteArray()
        val transport = QueuedSessionTransport()
        val feedStarted = CountDownLatch(1)
        val releaseFeed = CountDownLatch(1)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(
                outputs = CopyOnWriteArrayList(),
                onFeed = { sequence, _ ->
                    if (sequence == 1L) {
                        feedStarted.countDown()
                        check(releaseFeed.await(2, TimeUnit.SECONDS)) {
                            "timed out releasing blocked live feed"
                        }
                    }
                },
            ),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "https://example.test",
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
            transport = transport,
            reconnectDelaysMillis = listOf(0),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
        )
        val output = incomingOutputEnvelope(
            sessionId,
            pcId,
            androidId,
            pcIdentity,
            androidIdentity,
            keyId,
            1,
            byteArrayOf(1),
        )

        try {
            client.start()
            withTimeout(2_000) { client.sessionState.first { it is RemoteSessionState.Live } }
            transport.deliver(output, generation = 1)
            assertTrue(withContext(Dispatchers.IO) { feedStarted.await(2, TimeUnit.SECONDS) })

            transport.emitFailed("offline")
            withTimeout(2_000) {
                while (transport.connectCount < 2 ||
                    client.transportState.value !is ProductionTransportState.Connected ||
                    !client.diagnostics().subscriberReady ||
                    transport.sent.count { it.messageType == MessageType.SUBSCRIBE } < 2 ||
                    transport.sent.count { it.messageType == MessageType.FLOW_CONTROL } < 2
                ) {
                    kotlinx.coroutines.yield()
                }
            }

            assertEquals(1L, releaseFeed.count)
            assertEquals(2, transport.connectCount)
            assertEquals(2, transport.currentConnectedGeneration())
            assertTrue(client.diagnostics().subscriberReady)
            assertTrue(client.diagnostics().transportCollectorActive)
        } finally {
            releaseFeed.countDown()
            client.close()
            keyId.fill(0)
            androidIdentity.destroy()
            pcIdentity.destroy()
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
            transport = testTransport(
                server.url("/").toString(),
                androidId,
                androidIdentity,
                sessionId,
                parentScope = singleScope,
            ),
            acknowledgementObserver = { generation, sequence ->
                ackStarted.complete(Unit)
                releaseAck.await()
                DurableAckCommit(sequence, generation)
            },
            acknowledgementResetObserver = ::resetAcknowledgement,
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            reconnectDelaysMillis = listOf(0),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
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
    fun restoresExplicitControlIntentAfterPhysicalGenerationReconnect(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val firstLeaseId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
        val restoredLeaseId = UUID.fromString("11234567-89ab-cdef-0123-456789abcdef")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val firstSubscribed = CompletableDeferred<Unit>()
        val restoredSubscribed = CompletableDeferred<Unit>()
        val firstSocket = CompletableDeferred<WebSocket>()

        fun realtime(
            subscribed: CompletableDeferred<Unit>,
            connected: CompletableDeferred<WebSocket>? = null,
        ) = object : WebSocketListener() {
            override fun onMessage(webSocket: WebSocket, text: String) {
                connected?.complete(webSocket)
                check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
            }

            override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                if (Envelope.parseFrom(bytes.toByteArray()).messageType == MessageType.SUBSCRIBE) {
                    subscribed.complete(Unit)
                }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }
        }

        server.enqueue(MockResponse().withWebSocketUpgrade(realtime(firstSubscribed, firstSocket)))
        server.enqueue(
            MockResponse()
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """{"lease_id":"$firstLeaseId","expires_at":"2099-01-01T00:00:00Z","version":1}""",
                ),
        )
        server.enqueue(MockResponse().withWebSocketUpgrade(realtime(restoredSubscribed)))
        server.enqueue(
            MockResponse()
                .setHeader("Content-Type", "application/json")
                .setBody(
                    """{"lease_id":"$restoredLeaseId","expires_at":"2099-01-01T00:00:00Z","version":2}""",
                ),
        )
        server.start()

        val multiplexer = ProductionRealtimeMultiplexer(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = androidIdentity,
            parentScope = scope,
            allowCleartextForLocalTest = true,
        ).also(multiplexers::add)
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
            transport = multiplexer.register(sessionId),
            reconnectDelaysMillis = listOf(0),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )
        try {
            client.start()
            withTimeout(2_000) { firstSubscribed.await() }
            assertEquals("/v1/realtime", server.takeRequest(2, TimeUnit.SECONDS)?.path)
            client.requestControl()
            assertEquals(
                firstLeaseId.toString(),
                assertIs<RemoteSessionState.Controlling>(client.sessionState.value).leaseId,
            )
            assertEquals("/v1/sessions/$sessionId/lease", server.takeRequest(2, TimeUnit.SECONDS)?.path)

            firstSocket.await().close(1011, "physical relay failure")

            withTimeout(5_000) { restoredSubscribed.await() }
            val restored = withTimeout(5_000) {
                client.sessionState.first {
                    it is RemoteSessionState.Controlling && it.leaseId == restoredLeaseId.toString()
                }
            }
            assertEquals(2L, assertIs<RemoteSessionState.Controlling>(restored).leaseVersion)
            assertEquals("/v1/realtime", server.takeRequest(2, TimeUnit.SECONDS)?.path)
            assertEquals("/v1/sessions/$sessionId/lease", server.takeRequest(2, TimeUnit.SECONDS)?.path)
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
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
        val durableAcks = CopyOnWriteArrayList<DurableAckCommit>()
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = { generation, sequence ->
                DurableAckCommit(sequence, generation).also(durableAcks::add)
            },
            acknowledgementResetObserver = ::resetAcknowledgement,
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
                while (durableAcks.lastOrNull()?.sequence != 2L || wireAcks.lastOrNull() != 2L) {
                    kotlinx.coroutines.yield()
                }
            }
            assertDurableCursor(durableAcks, expectedFinal = 2L, expectedGeneration = 1L)
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
        val durableAcks = CopyOnWriteArrayList<DurableAckCommit>()
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
            transport = testTransport(server.url("/").toString(), androidId, androidIdentity, sessionId),
            acknowledgementObserver = { generation, sequence ->
                DurableAckCommit(sequence, generation).also(durableAcks::add)
            },
            acknowledgementResetObserver = ::resetAcknowledgement,
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
            assertDurableCursor(durableAcks, expectedFinal = 4L, expectedGeneration = 1L)
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

    @Test
    fun inputAcceptanceTimeoutDoesNotDisconnectHealthyTransportOrLease(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val transport = RecordingSessionTransport(sendResult = true)
        val client = ProductionRealtimeSessionClient(
            engine = RecordingEngine(CopyOnWriteArrayList()),
            config = ProductionRealtimeSessionConfig(
                rootUrl = "http://127.0.0.1:1/",
                tokenProvider = AccessTokenProvider { "oidc-token" },
                androidDeviceId = androidId,
                pcDeviceId = pcId,
                sessionId = sessionId,
                keyEpoch = 7,
                pcExchangePublicKey = pcIdentity.exchange.publicKey.copyOf(),
            ),
            identity = androidIdentity,
            parentScope = scope,
            transport = transport,
            inputAcceptanceTimeoutMillis = 25,
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )
        try {
            client.remoteSession.onConnected()
            client.remoteSession.grantControl("lease", Long.MAX_VALUE, leaseVersion = 1)

            val result = client.remoteSession.submitInput("dock-shortcut".encodeToByteArray())

            assertIs<InputSubmission.DeliveryUnknown>(result)
            assertTrue(transport.sent.any { it.messageType == MessageType.INPUT_EVENT })
            assertTrue(transport.disconnectReasons.isEmpty())
            assertIs<RemoteSessionState.Controlling>(client.sessionState.value)
        } finally {
            client.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun leaseRenewalDistinguishesAuthorityLossFromTransientServerFailure(): Unit = runBlocking {
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val leaseId = UUID.fromString("01234567-89ab-cdef-0123-456789abcdef")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        server.enqueue(MockResponse().setResponseCode(204))
        server.enqueue(
            MockResponse()
                .setResponseCode(409)
                .setHeader("Content-Type", "application/json")
                .setBody("""{"error":{"code":"LEASE_NOT_HELD"}}"""),
        )
        server.enqueue(
            MockResponse()
                .setResponseCode(503)
                .setHeader("Content-Type", "application/json")
                .setBody("""{"error":{"code":"LEASE_UNAVAILABLE"}}"""),
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
            transport = RecordingSessionTransport(sendResult = true),
            acknowledgementObserver = ::commitAcknowledgement,
            acknowledgementResetObserver = ::resetAcknowledgement,
            allowCleartextForLocalTest = true,
        )
        try {
            val missing = assertFailsWith<RemoteControlLeaseLostException> {
                client.renewLease(leaseId.toString(), 1)
            }
            assertEquals("LEASE_NOT_HELD", missing.reasonCode)
            val replaced = assertFailsWith<RemoteControlLeaseLostException> {
                client.renewLease(leaseId.toString(), 1)
            }
            assertEquals("LEASE_NOT_HELD", replaced.reasonCode)
            val transient = assertFailsWith<TermLinkApiException> {
                client.renewLease(leaseId.toString(), 1)
            }
            assertEquals(503, transient.status)
            assertEquals("LEASE_UNAVAILABLE", transient.code)
        } finally {
            client.close()
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

    private fun assertDurableCursor(
        values: List<DurableAckCommit>,
        expectedFinal: Long,
        expectedGeneration: Long,
    ) {
        assertCumulativeCursor(values.map(DurableAckCommit::sequence), expectedFinal)
        assertTrue(values.all { it.rendererGeneration == expectedGeneration })
    }

    private suspend fun commitAcknowledgement(generation: Long, sequence: Long): DurableAckCommit =
        DurableAckCommit(sequence = sequence, rendererGeneration = generation)

    private suspend fun resetAcknowledgement(generation: Long): DurableAckCommit =
        DurableAckCommit(sequence = 0L, rendererGeneration = generation + 1L)

    private fun incomingHeartbeatEnvelope(
        sessionId: UUID,
        pcId: UUID,
        androidId: UUID,
        pcIdentity: DeviceIdentity,
        androidIdentity: DeviceIdentity,
        keyId: ByteArray,
        sequence: Long,
    ): Envelope {
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
        val nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), sequence.toULong())
        return try {
            ProductionRealtimeClient.encryptedEnvelope(
                sessionId = sessionId,
                senderId = pcId,
                messageType = MessageType.HEARTBEAT,
                direction = CryptoDirection.PC_TO_ANDROID,
                sequence = sequence,
                acknowledgement = 0,
                keyId = keyId,
                nonce = nonce,
                sessionKey = sessionKey,
                plaintext = ByteArray(0),
                recipientId = androidId,
            )
        } finally {
            nonce.fill(0)
            sessionKey.fill(0)
            pairwise.fill(0)
        }
    }

    private fun incomingOutputEnvelope(
        sessionId: UUID,
        pcId: UUID,
        androidId: UUID,
        pcIdentity: DeviceIdentity,
        androidIdentity: DeviceIdentity,
        keyId: ByteArray,
        sequence: Long,
        output: ByteArray,
    ): Envelope {
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
        val nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), sequence.toULong())
        val plaintext = OutputChunkPayload.newBuilder()
            .setOutput(ByteString.copyFrom(output))
            .setSourceEventSeq(sequence)
            .setPresentationRevision(sequence)
            .build()
            .toByteArray()
        return try {
            ProductionRealtimeClient.encryptedEnvelope(
                sessionId = sessionId,
                senderId = pcId,
                messageType = MessageType.OUTPUT_CHUNK,
                direction = CryptoDirection.PC_TO_ANDROID,
                sequence = sequence,
                acknowledgement = 0,
                keyId = keyId,
                nonce = nonce,
                sessionKey = sessionKey,
                plaintext = plaintext,
                recipientId = androidId,
            )
        } finally {
            plaintext.fill(0)
            nonce.fill(0)
            sessionKey.fill(0)
            pairwise.fill(0)
        }
    }

    private class GenerationRaceSessionTransport : ProductionRealtimeSessionTransport {
        data class BlockedGenerationSend(
            val messageType: MessageType,
            val remainingMatches: AtomicInteger = AtomicInteger(1),
            val entered: CountDownLatch = CountDownLatch(1),
            val release: CountDownLatch = CountDownLatch(1),
        )

        private val lock = Any()
        private val incoming = Channel<ProductionReceivedEnvelope>(Channel.UNLIMITED)
        private val mutablePressure = MutableStateFlow(ProductionReceivePressure(0, false, 0, 0))
        private val mutableState = MutableSharedFlow<RealtimeState>(replay = 1, extraBufferCapacity = 4)
        private val mutableConnectionEvents =
            MutableSharedFlow<ProductionRealtimeSessionConnectionEvent>(replay = 1, extraBufferCapacity = 4)
        private val blockedGenerationSend = AtomicReference<BlockedGenerationSend?>()
        private val failedGenerationSend = AtomicReference<MessageType?>()
        private val generationReads = AtomicInteger(0)
        private val conditionalDisconnects = AtomicInteger(0)
        private val connections = AtomicInteger(0)
        private var connectedGeneration = 0
        val sent = CopyOnWriteArrayList<Envelope>()
        val generationBoundSends = CopyOnWriteArrayList<Pair<MessageType, Int>>()
        val generationSendAttempts = CopyOnWriteArrayList<Pair<MessageType, Int>>()
        val disconnectReasons = CopyOnWriteArrayList<String>()

        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incoming.receiveAsFlow()
        override val receivePressure = mutablePressure
        override val state = mutableState
        override val connectionEvents: Flow<ProductionRealtimeSessionConnectionEvent> =
            mutableConnectionEvents

        val subscribeCount: Int
            get() = sent.count { it.messageType == MessageType.SUBSCRIBE }

        val connectedGenerationReads: Int
            get() = generationReads.get()

        val conditionalDisconnectAttempts: Int
            get() = conditionalDisconnects.get()

        val connectCount: Int
            get() = connections.get()

        override fun connect() {
            connections.incrementAndGet()
            val generation = synchronized(lock) {
                connectedGeneration = if (connectedGeneration <= 0) 1 else connectedGeneration + 1
                connectedGeneration
            }
            emitConnectedEvent(generation)
        }

        override fun send(envelope: Envelope): Boolean {
            return synchronized(lock) {
                if (connectedGeneration <= 0) {
                    false
                } else {
                    sent += envelope
                    true
                }
            }
        }

        override fun sendForConnectedGeneration(
            envelope: Envelope,
            expectedGeneration: Int,
        ): Boolean {
            val candidate = blockedGenerationSend.get()
            val blocked = candidate?.takeIf { it.messageType == envelope.messageType }
                ?.takeIf { it.remainingMatches.decrementAndGet() == 0 }
                ?.takeIf { blockedGenerationSend.compareAndSet(candidate, null) }
            if (blocked != null) {
                blocked.entered.countDown()
                check(blocked.release.await(2, TimeUnit.SECONDS)) { "timed out releasing blocked SUBSCRIBE" }
            }
            return synchronized(lock) {
                if (expectedGeneration <= 0 || connectedGeneration != expectedGeneration) {
                    false
                } else {
                    generationSendAttempts += envelope.messageType to expectedGeneration
                    if (failedGenerationSend.compareAndSet(envelope.messageType, null)) {
                        return@synchronized false
                    }
                    sent += envelope
                    generationBoundSends += envelope.messageType to expectedGeneration
                    true
                }
            }
        }

        override fun disconnect(reason: String) {
            synchronized(lock) { connectedGeneration = 0 }
            disconnectReasons += reason
        }

        override fun currentConnectedGeneration(): Int {
            generationReads.incrementAndGet()
            return synchronized(lock) { connectedGeneration }
        }

        override fun disconnectIfCurrentGeneration(
            expectedGeneration: Int,
            reason: String,
        ): Boolean {
            conditionalDisconnects.incrementAndGet()
            return synchronized(lock) {
                if (expectedGeneration <= 0 || connectedGeneration != expectedGeneration) {
                    false
                } else {
                    connectedGeneration = 0
                    disconnectReasons += reason
                    true
                }
            }
        }

        override fun close() {
            synchronized(lock) { connectedGeneration = 0 }
            incoming.close()
        }

        fun blockNextSubscribe(): BlockedGenerationSend =
            blockNextGenerationSend(MessageType.SUBSCRIBE)

        fun blockNextGenerationSend(messageType: MessageType): BlockedGenerationSend =
            BlockedGenerationSend(messageType).also { blocked ->
                check(blockedGenerationSend.compareAndSet(null, blocked))
            }

        fun blockNthGenerationSend(
            messageType: MessageType,
            occurrence: Int,
        ): BlockedGenerationSend {
            require(occurrence > 0) { "occurrence must be positive" }
            return BlockedGenerationSend(
                messageType = messageType,
                remainingMatches = AtomicInteger(occurrence),
            ).also { blocked ->
                check(blockedGenerationSend.compareAndSet(null, blocked))
            }
        }

        fun failNextGenerationSend(messageType: MessageType) {
            check(failedGenerationSend.compareAndSet(null, messageType))
        }

        fun replaceGeneration(): Int = synchronized(lock) {
            check(connectedGeneration > 0)
            connectedGeneration++
            connectedGeneration
        }

        fun emitConnectedEvent(generation: Int) {
            check(generation > 0)
            check(
                mutableConnectionEvents.tryEmit(
                    ProductionRealtimeSessionConnectionEvent(RealtimeState.Connected, generation),
                ),
            )
            check(mutableState.tryEmit(RealtimeState.Connected))
        }

        fun emitPressure(paused: Boolean) {
            val generation = synchronized(lock) { connectedGeneration }
            mutablePressure.value = ProductionReceivePressure(
                generation = generation,
                paused = paused,
                bufferedEvents = if (paused) 1 else 0,
                bufferedWireBytes = if (paused) 1 else 0,
            )
        }

        fun deliver(envelope: Envelope, generation: Int) {
            check(
                incoming.trySend(
                    ProductionReceivedEnvelope(
                        envelope = envelope,
                        wireBytes = envelope.serializedSize,
                        generation = generation,
                        releaseReservation = {},
                    ),
                ).isSuccess,
            )
        }
    }

    private class QueuedSessionTransport : ProductionRealtimeSessionTransport {
        private val incoming = Channel<ProductionReceivedEnvelope>(
            capacity = Channel.UNLIMITED,
            onUndeliveredElement = ProductionReceivedEnvelope::close,
        )
        private val mutablePressure = MutableStateFlow(ProductionReceivePressure(0, false, 0, 0))
        private val mutableState = MutableSharedFlow<RealtimeState>(replay = 1, extraBufferCapacity = 4)
        private val releaseCount = AtomicInteger(0)
        private val connectionCount = AtomicInteger(0)
        private val generation = AtomicInteger(0)
        private val connectedGeneration = AtomicInteger(0)
        private val gracefulCloses = AtomicInteger(0)
        private val immediateCloses = AtomicInteger(0)
        val sent = CopyOnWriteArrayList<Envelope>()
        val disconnectReasons = CopyOnWriteArrayList<String>()

        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = incoming.receiveAsFlow()
        override val receivePressure = mutablePressure
        override val state = mutableState

        val releasedReservations: Int
            get() = releaseCount.get()

        val connectCount: Int
            get() = connectionCount.get()

        val gracefulCloseCount: Int
            get() = gracefulCloses.get()

        val immediateCloseCount: Int
            get() = immediateCloses.get()

        override fun connect() {
            connectionCount.incrementAndGet()
            connectedGeneration.set(generation.incrementAndGet())
            check(mutableState.tryEmit(RealtimeState.Connected))
        }

        override fun send(envelope: Envelope): Boolean {
            sent += envelope
            return true
        }

        override fun disconnect(reason: String) {
            connectedGeneration.set(0)
            disconnectReasons += reason
        }

        override fun currentConnectedGeneration(): Int = connectedGeneration.get()

        override fun disconnectIfCurrentGeneration(expectedGeneration: Int, reason: String): Boolean =
            synchronized(this) {
                if (expectedGeneration <= 0 || connectedGeneration.get() != expectedGeneration) {
                    false
                } else {
                    connectedGeneration.set(0)
                    disconnectReasons += reason
                    true
                }
            }

        override fun close() {
            immediateCloses.incrementAndGet()
            incoming.close()
        }

        override fun closeAfterUnsubscribe() {
            gracefulCloses.incrementAndGet()
            incoming.close()
        }

        fun emitConnected() {
            connectedGeneration.set(generation.incrementAndGet())
            check(mutableState.tryEmit(RealtimeState.Connected))
        }

        fun emitFailed(reason: String) {
            connectedGeneration.set(0)
            check(mutableState.tryEmit(RealtimeState.Failed(reason)))
        }

        fun emitState(state: RealtimeState) {
            if (state is RealtimeState.Connected) {
                connectedGeneration.set(generation.incrementAndGet())
            } else if (state is RealtimeState.Connecting || state is RealtimeState.Reconnecting ||
                state is RealtimeState.AuthenticationRejected ||
                state is RealtimeState.AuthenticationFailed || state is RealtimeState.Failed ||
                state is RealtimeState.Closed
            ) {
                connectedGeneration.set(0)
            }
            check(mutableState.tryEmit(state))
        }

        fun deliver(envelope: Envelope, generation: Int) {
            check(
                incoming.trySend(
                    ProductionReceivedEnvelope(
                        envelope = envelope,
                        wireBytes = envelope.serializedSize,
                        generation = generation,
                        releaseReservation = { releaseCount.incrementAndGet() },
                    ),
                ).isSuccess,
            )
        }
    }

    private class RecordingSessionTransport(
        private val sendResult: Boolean,
    ) : ProductionRealtimeSessionTransport {
        override val receivedEnvelopes: Flow<ProductionReceivedEnvelope> = emptyFlow()
        override val receivePressure = MutableStateFlow(ProductionReceivePressure(0, false, 0, 0))
        override val state = MutableSharedFlow<RealtimeState>(extraBufferCapacity = 1)
        val sent = CopyOnWriteArrayList<Envelope>()
        val disconnectReasons = CopyOnWriteArrayList<String>()

        override fun connect() = Unit

        override fun send(envelope: Envelope): Boolean {
            sent += envelope
            return sendResult
        }

        override fun disconnect(reason: String) {
            disconnectReasons += reason
        }

        override fun currentConnectedGeneration(): Int = 1

        override fun disconnectIfCurrentGeneration(expectedGeneration: Int, reason: String): Boolean {
            if (expectedGeneration != 1) return false
            disconnectReasons += reason
            return true
        }

        override fun close() = Unit
    }

    private class RecordingEngine(
        private val outputs: MutableList<ByteArray>,
        private val onDispose: () -> Unit = {},
        private val onFeed: (Long, Thread) -> Unit = { _, _ -> },
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
            onFeed(seq, Thread.currentThread())
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

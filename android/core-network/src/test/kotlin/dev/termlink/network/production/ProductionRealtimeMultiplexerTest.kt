package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.session.DurableAckCommit
import dev.termlink.session.RendererBootstrapState
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
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString as OkioByteString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProductionRealtimeMultiplexerTest {
    @Test
    fun aiRepliesCanArriveOutOfOrderWithoutBlockingCommandsOrCatalog(): Unit = runTest {
        val socket = FakeSocket(); val pc = UUID.randomUUID()
        val mux = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val read = mux.register(pc, catalogOnly = true, aiOnly = true, aiSequence = 101)
        val command = mux.register(pc, catalogOnly = true, aiOnly = true, aiSequence = 102)
        val catalog = mux.register(pc, catalogOnly = true)
        read.connect(); socket.emitState(RealtimeState.Connected); runCurrent()
        fun envelope(type: MessageType, seq: Long) = Envelope.newBuilder()
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pc))).setMessageType(type).setSeq(seq).build()
        assertTrue(read.sendForConnectedGeneration(envelope(MessageType.AI_AGENT_REQUEST, 101), 1))
        assertTrue(command.sendForConnectedGeneration(envelope(MessageType.AI_AGENT_REQUEST, 102), 1))
        assertTrue(catalog.sendForConnectedGeneration(envelope(MessageType.SESSION_CATALOG_REQUEST, 103), 1))
        val a = async { read.receivedEnvelopes.first() }; val b = async { command.receivedEnvelopes.first() }
        fun deliver(type: MessageType, seq: Long) = socket.deliver(ProductionReceivedEnvelope(envelope(type, seq), 64, 1, releaseReservation = {}))
        deliver(MessageType.AI_AGENT_RESPONSE, 102); runCurrent()
        assertTrue(b.isCompleted); assertTrue(!a.isCompleted); b.await().close(); command.close()
        deliver(MessageType.AI_AGENT_RESPONSE, 102) // Late duplicate cannot reach another request.
        deliver(MessageType.AI_AGENT_RESPONSE, 101); runCurrent()
        assertEquals(101L, a.await().envelope.seq); a.await().close()
        val c = async { catalog.receivedEnvelopes.first() }
        deliver(MessageType.SESSION_CATALOG_RESPONSE, 103); runCurrent()
        c.await().close(); assertEquals(0, socket.disconnectCount)
        mux.close()
    }

    @Test
    fun optionalResizeRequiresCapabilityOnCurrentServerGeneration(): Unit = runTest {
        val socket = FakeSocket()
        val mux = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val capability = "terminal_window_resize_v1"
        socket.activateGeneration(1)
        assertTrue(!mux.hasNegotiatedCapability(capability))
        socket.negotiatedCapabilities.value = ProductionRealtimeNegotiatedCapabilities(1, setOf(capability))
        assertTrue(mux.hasNegotiatedCapability(capability))
        socket.activateGeneration(2)
        assertTrue(!mux.hasNegotiatedCapability(capability))
        socket.negotiatedCapabilities.value = ProductionRealtimeNegotiatedCapabilities(2, emptySet())
        assertTrue(!mux.hasNegotiatedCapability(capability))
        mux.close()
    }

    @Test
    fun pendingCleanupDoesNotBlockCatalogAndClosingItPreservesCatalogRoute(): Unit = runTest {
        val socket = FakeSocket()
        val pcId = UUID.randomUUID()
        val mux = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val cleanup = mux.register(pcId, catalogOnly = true, cleanupOnly = true)
        val catalog = mux.register(pcId, catalogOnly = true)
        cleanup.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        fun envelope(type: MessageType) = Envelope.newBuilder()
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pcId)))
            .setMessageType(type).setSeq(1).build()
        assertTrue(cleanup.sendForConnectedGeneration(envelope(MessageType.SESSION_CLEANUP_REQUEST), 1))
        assertTrue(catalog.sendForConnectedGeneration(envelope(MessageType.SESSION_CATALOG_REQUEST), 1))
        val pendingCleanup = async { cleanup.receivedEnvelopes.first() }
        val pendingCatalog = async { catalog.receivedEnvelopes.first() }
        socket.deliver(ProductionReceivedEnvelope(envelope(MessageType.SESSION_CATALOG_RESPONSE), 64, 1, releaseReservation = {}))
        runCurrent()
        assertTrue(pendingCatalog.isCompleted)
        assertTrue(!pendingCleanup.isCompleted)
        pendingCatalog.await().close()
        cleanup.close()
        pendingCleanup.cancel()
        val next = async { catalog.receivedEnvelopes.first() }
        socket.deliver(ProductionReceivedEnvelope(envelope(MessageType.SESSION_CLEANUP_RESPONSE), 64, 1, releaseReservation = {}))
        socket.deliver(ProductionReceivedEnvelope(envelope(MessageType.SESSION_CATALOG_RESPONSE), 64, 1, releaseReservation = {}))
        runCurrent()
        assertEquals(MessageType.SESSION_CATALOG_RESPONSE, next.await().envelope.messageType)
        next.await().close()
        assertEquals(0, socket.disconnectCount)
        mux.close()
    }

    @Test
    fun catalogEndpointRejectsTerminalFramesAndOldGenerationWithoutTerminalSubscription(): Unit = runTest {
        val socket = FakeSocket()
        val pcId = UUID.randomUUID()
        val mux = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = mux.register(pcId, catalogOnly = true, cleanupOnly = true)
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        val request = Envelope.newBuilder().setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pcId)))
            .setMessageType(MessageType.SESSION_CLEANUP_REQUEST).setSeq(1).build()
        assertTrue(endpoint.sendForConnectedGeneration(request, 1))
        val delivered = async { endpoint.receivedEnvelopes.first() }
        fun frame(type: MessageType, generation: Int) = ProductionReceivedEnvelope(
            request.toBuilder().setMessageType(type).build(), 64, generation,
            releaseReservation = { socket.releasedReservations.incrementAndGet() },
        )
        socket.deliver(frame(MessageType.OUTPUT_CHUNK, 1))
        runCurrent()
        assertTrue(!delivered.isCompleted)
        socket.activateGeneration(2)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        assertTrue(endpoint.sendForConnectedGeneration(request.toBuilder().setSeq(2).build(), 2))
        socket.deliver(frame(MessageType.SESSION_CLEANUP_RESPONSE, 1))
        runCurrent()
        assertTrue(!delivered.isCompleted)
        socket.deliver(frame(MessageType.SESSION_CLEANUP_RESPONSE, 2))
        runCurrent()
        delivered.await().close()
        assertEquals(listOf(MessageType.SESSION_CLEANUP_REQUEST, MessageType.SESSION_CLEANUP_REQUEST), socket.sentEnvelopes.map { it.messageType })
        assertTrue(socket.sentEnvelopes.all { it.ack == 0L })
        endpoint.close()
        assertEquals(0, socket.disconnectCount)
        mux.close()
    }

    @Test
    fun endpointReservationRetainsExactWireBytesUntilConsumerRelease(): Unit = runTest {
        val socket = FakeSocket()
        val sessionId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(sessionId)

        val canonical = incomingEnvelope(
            sessionId = sessionId,
            senderId = UUID.randomUUID(),
            recipientId = UUID.randomUUID(),
            sequence = 1,
        )
        val exact = byteArrayOf(0x98.toByte(), 0x06, 0x7b) + canonical.toByteArray()
        val expected = exact.copyOf()
        val delivered = async {
            endpoint.receivedEnvelopes.first().also { received ->
                assertArrayEquals(expected, received.exactBytes)
            }
        }

        socket.deliver(
            ProductionReceivedEnvelope(
                envelope = Envelope.parseFrom(exact),
                wireBytes = exact.size,
                generation = 1,
                releaseReservation = { socket.releasedReservations.incrementAndGet() },
                rawBytes = exact,
            ),
        )
        runCurrent()

        val endpointEnvelope = delivered.await()
        assertArrayEquals(expected, exact)
        endpointEnvelope.close()
        assertTrue(exact.all { it == 0.toByte() })
        assertEquals(1, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun endpointExactSendPreservesBytesAndGenerationGate(): Unit = runTest {
        val socket = FakeSocket()
        val sessionId = UUID.randomUUID()
        val senderId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        val envelope = Envelope.newBuilder()
            .setProtocolVersion(1)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setSenderDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(senderId)))
            .setDirection(Direction.ANDROID_TO_PC)
            .setMessageType(MessageType.INPUT_EVENT)
            .setSeq(1)
            .build()
        val exact = byteArrayOf(0x98.toByte(), 0x06, 0x7b) + envelope.toByteArray()
        val parsed = Envelope.parseFrom(exact)

        assertTrue(endpoint.sendExactForConnectedGeneration(parsed, exact, expectedGeneration = 1))
        assertArrayEquals(exact, socket.sentExactBytes.single())
        socket.activateGeneration(2)
        assertTrue(!endpoint.sendExactForConnectedGeneration(parsed, exact, expectedGeneration = 1))
        assertEquals(1, socket.sentExactBytes.size)
        multiplexer.close()
    }

    @Test
    fun endpointRelayExactBypassesDirectGatewayAndPreservesBytes(): Unit = runTest {
        val socket = FakeSocket()
        val sessionId = UUID.randomUUID()
        val peerDeviceId = UUID.randomUUID()
        val senderId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId, peerDeviceId)
        val directOffers = AtomicInteger(0)
        val directHandle = multiplexer.installDirectDataGateway(
            object : ProductionRealtimeDirectDataGateway {
                override fun offerRelay(
                    peerDeviceId: UUID,
                    received: ProductionReceivedEnvelope,
                ): Boolean = false

                override fun routeEligibleOutbound(
                    peerDeviceId: UUID,
                    envelope: Envelope,
                    exactEnvelopeBytes: ByteArray,
                    relayFallback: () -> Boolean,
                ): Boolean {
                    directOffers.incrementAndGet()
                    return relayFallback()
                }
            },
        )
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        val envelope = Envelope.newBuilder()
            .setProtocolVersion(1)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setSenderDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(senderId)))
            .setRecipientDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(peerDeviceId)))
            .setDirection(Direction.ANDROID_TO_PC)
            .setMessageType(MessageType.INPUT_EVENT)
            .setSeq(1)
            .build()
        val exact = byteArrayOf(0x98.toByte(), 0x06, 0x7b) + envelope.toByteArray()
        val parsed = Envelope.parseFrom(exact)

        assertTrue(endpoint.sendRelayExact(parsed, exact))
        assertEquals(0, directOffers.get())
        assertArrayEquals(exact, socket.sentExactBytes.single())

        directHandle.close()
        multiplexer.close()
    }

    @Test
    fun endpointRelayExactHonorsGenerationAndClosedFences(): Unit = runTest {
        val socket = FakeSocket()
        val sessionId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId, UUID.randomUUID())
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        val envelope = Envelope.newBuilder()
            .setProtocolVersion(1)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setDirection(Direction.ANDROID_TO_PC)
            .setMessageType(MessageType.INPUT_EVENT)
            .setSeq(1)
            .build()
        val exact = envelope.toByteArray()

        assertTrue(endpoint.sendRelayExactForConnectedGeneration(envelope, exact, expectedGeneration = 1))
        socket.activateGeneration(2)
        assertTrue(!endpoint.sendRelayExactForConnectedGeneration(envelope, exact, expectedGeneration = 1))
        assertEquals(1, socket.sentExactBytes.size)

        endpoint.close()
        val closedFailure = runCatching { endpoint.sendRelayExact(envelope, exact) }.exceptionOrNull()
        assertTrue(closedFailure is IllegalStateException)
        assertEquals(1, socket.sentExactBytes.size)
        multiplexer.close()
    }

    @Test
    fun directObserverSeesRelayGenerationWithoutReceivingRelayOwnership(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        val connected = async {
            multiplexer.directRelayConnectionEvents.first {
                it.state is RealtimeState.Connected
            }
        }

        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        assertEquals(1, connected.await().connectedGeneration)
        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        multiplexer.close()
    }

    @Test
    fun deviceSupervisorCoalescesEndpointsBacksOffAndCloseCancelsRetry(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(
            socket = socket,
            parentScope = backgroundScope,
            retryJitter = ProductionRealtimeRetryJitter { it },
            stableConnectionMillis = 1_000,
        )
        val endpointA = multiplexer.register(UUID.randomUUID())
        val endpointB = multiplexer.register(UUID.randomUUID())
        val reconnectAttempts = CopyOnWriteArrayList<Int>()
        backgroundScope.launch {
            endpointA.state.collect { state ->
                if (state is RealtimeState.Reconnecting && state.attempt > 0) {
                    reconnectAttempts += state.attempt
                }
            }
        }

        endpointA.connect()
        endpointB.connect()
        assertEquals(1, socket.connectCount)
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        socket.emitState(RealtimeState.Failed("offline"))
        runCurrent()
        endpointA.connect()
        endpointB.connect()
        advanceTimeBy(499)
        runCurrent()
        assertEquals(1, socket.connectCount)
        advanceTimeBy(1)
        runCurrent()
        assertEquals(2, socket.connectCount)

        socket.emitState(RealtimeState.Connected)
        runCurrent()
        advanceTimeBy(500) // Too short to reset the retry sequence.
        socket.emitState(RealtimeState.Failed("offline again"))
        runCurrent()
        advanceTimeBy(999)
        runCurrent()
        assertEquals(2, socket.connectCount)

        multiplexer.onNetworkAvailable() // Bypasses the remaining retry delay.
        multiplexer.onAppForegrounded(20_000, networkUsable = true) // Coalesces with that owner.
        assertEquals(3, socket.connectCount)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        advanceTimeBy(1_000) // A healthy window resets the next attempt to one.
        runCurrent()
        socket.emitState(RealtimeState.Failed("offline after stable window"))
        runCurrent()

        assertEquals(listOf(1, 2, 1), reconnectAttempts.toList())
        multiplexer.close()
        advanceTimeBy(30_000)
        runCurrent()
        assertEquals(3, socket.connectCount)
        assertEquals(1, socket.closeCount)
    }

    @Test
    fun foregroundReconcileBypassesRetryDelayAndCoalescesWithConnectOwner(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        val observedStates = CopyOnWriteArrayList<RealtimeState>()
        backgroundScope.launch { endpoint.state.collect(observedStates::add) }
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        socket.emitState(RealtimeState.Failed("offline"))
        runCurrent()
        endpoint.connect() // Coalesces with the one supervisor retry owner.
        multiplexer.onAppForegrounded(20_000, networkUsable = true) // Bypasses retry delay.
        multiplexer.onAppForegrounded(20_000, networkUsable = true) // Coalesces while connecting.
        observedStates.clear()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        assertEquals(2, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(RealtimeState.Connected, observedStates.last())
        multiplexer.close()
    }

    @Test
    fun subscribeSendAndGenerationRotateShareOneAtomicGate(): Unit = runTest {
        val socket = FakeSocket()
        socket.livenessProbeSupported = true
        socket.nextLivenessProbe = CompletableDeferred(false)
        val sessionId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        socket.blockNextSend(entered, release)
        val subscribe = async(Dispatchers.Default) { endpoint.sendSubscribe(sessionId, ack = 17) }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val rotate = async(Dispatchers.Default) {
            multiplexer.onAppForegrounded(20_000, networkUsable = true)
        }

        release.countDown()
        subscribe.await()
        rotate.await()
        socket.deliver(socket.received(sessionId, 18, generation = 1))
        socket.deliver(socket.received(sessionId, 18, generation = 3))
        runCurrent()

        assertEquals(1L, endpoint.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(1L, endpoint.diagnostics().routeDecisions.endpointNotSubscribed)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(sessionId, ack = 17)
        val received = async {
            endpoint.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 18, generation = 3))
        runCurrent()
        assertEquals(listOf(18L), received.await())
        multiplexer.close()
    }

    @Test
    fun generationBoundSubscribeCannotAttachAnOldHandlerToItsSuccessor(): Unit = runTest {
        val socket = FakeSocket()
        val sessionId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        val subscribe = Envelope.newBuilder()
            .setProtocolVersion(1)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setDirection(Direction.ANDROID_TO_PC)
            .setMessageType(MessageType.SUBSCRIBE)
            .build()

        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        assertTrue(endpoint.sendForConnectedGeneration(subscribe, expectedGeneration = 1))

        socket.activateGeneration(2)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        val sentBeforeStaleAttempt = socket.sentEnvelopes.size

        assertTrue(!endpoint.sendForConnectedGeneration(subscribe, expectedGeneration = 1))
        assertEquals(sentBeforeStaleAttempt, socket.sentEnvelopes.size)
        assertTrue(endpoint.sendForConnectedGeneration(subscribe, expectedGeneration = 2))
        multiplexer.close()
    }

    @Test
    fun generationBoundFlowControlCannotLeakIntoSuccessorSocket(): Unit = runTest {
        val socket = FakeSocket()
        val sessionId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        val flowControl = Envelope.newBuilder()
            .setProtocolVersion(1)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setDirection(Direction.ANDROID_TO_PC)
            .setMessageType(MessageType.FLOW_CONTROL)
            .build()

        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        assertTrue(endpoint.sendForConnectedGeneration(flowControl, expectedGeneration = 1))

        socket.activateGeneration(2)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        val sentBeforeStaleAttempt = socket.sentEnvelopes.size

        assertTrue(!endpoint.sendForConnectedGeneration(flowControl, expectedGeneration = 1))
        assertEquals(sentBeforeStaleAttempt, socket.sentEnvelopes.size)
        assertTrue(endpoint.sendForConnectedGeneration(flowControl, expectedGeneration = 2))
        multiplexer.close()
    }

    @Test
    fun lastEndpointCloseLinearizesAgainstClaimedConnectAndPreventsRevival(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        socket.blockNextConnect(entered, release)
        val connect = async(Dispatchers.Default) { endpoint.connect() }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val close = async(Dispatchers.Default) { endpoint.close() }

        release.countDown()
        connect.await()
        close.await()
        multiplexer.onNetworkAvailable()

        assertEquals(1, socket.connectCount)
        assertEquals(0, endpoint.diagnostics().endpointCount)
        multiplexer.close()
    }

    @Test
    fun ownerCloseLinearizesAgainstClaimedConnectAndClosedNeverRevives(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        socket.blockNextConnect(entered, release)
        val connect = async(Dispatchers.Default) { endpoint.connect() }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val close = async(Dispatchers.Default) { multiplexer.close() }

        release.countDown()
        connect.await()
        close.await()
        multiplexer.onNetworkAvailable()
        multiplexer.onAppForegrounded(20_000, networkUsable = true)

        assertEquals(1, socket.connectCount)
        assertEquals(1, socket.closeCount)
        assertEquals("CLOSED", endpoint.diagnostics().phase)
    }

    @Test
    fun gracefulReplacementDropsOldRouteUntilFreshSubscribeWithoutDisturbingPeer(): Unit = runTest {
        val socket = FakeSocket()
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)
        oldA.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        oldA.sendSubscribe(sessionA, ack = 5)
        endpointB.sendSubscribe(sessionB, ack = 9)
        oldA.sendUnsubscribe(sessionA)
        oldA.closeAfterUnsubscribe()

        socket.deliver(socket.received(sessionA, 1, generation = 1))
        runCurrent()
        val replacementA = multiplexer.register(sessionA)
        socket.deliver(socket.received(sessionA, 2, generation = 1))
        val peer = async {
            endpointB.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionB, 10, generation = 1))
        runCurrent()

        assertEquals(1L, replacementA.diagnostics().routeDecisions.endpointClosed)
        assertEquals(1L, replacementA.diagnostics().routeDecisions.endpointNotSubscribed)
        assertEquals(listOf(10L), peer.await())
        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.connectCount)

        replacementA.sendSubscribe(sessionA, ack = 5)
        val fresh = async {
            replacementA.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionA, 4, generation = 1))
        runCurrent()
        assertEquals(listOf(4L), fresh.await())
        assertEquals(listOf(5L, 9L, 5L), socket.sentEnvelopes
            .filter { it.messageType == MessageType.SUBSCRIBE }
            .map(Envelope::getAck))
        multiplexer.close()
    }

    @Test
    fun gracefulTombstoneOnlyToleratesFramesFromItsRetiredGeneration(): Unit = runTest {
        val socket = FakeSocket()
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)
        endpointA.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(sessionA)
        endpointB.sendSubscribe(sessionB)
        endpointA.sendUnsubscribe(sessionA)
        endpointA.closeAfterUnsubscribe()

        socket.deliver(socket.received(sessionA, sequence = 1, generation = 1))
        runCurrent()
        assertEquals(0, socket.disconnectCount)

        socket.activateGeneration(2)
        socket.deliver(socket.received(sessionA, sequence = 2, generation = 2))
        runCurrent()

        assertEquals(0, socket.disconnectCount)
        assertEquals(2L, endpointB.diagnostics().routeDecisions.endpointClosed)
        multiplexer.close()
    }

    @Test
    fun sharedGenerationRotateResubscribesEverySessionFromItsOwnDurableAck(): Unit = runTest {
        val socket = FakeSocket()
        val androidId = UUID.fromString("10000000-0000-0000-0000-000000000000")
        val pcId = UUID.fromString("20000000-0000-0000-0000-000000000000")
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val androidIdentity = DeviceIdentity.generate()
        val pcIdentity = DeviceIdentity.generate()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        fun client(sessionId: UUID, ack: Long) = ProductionRealtimeSessionClient(
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
            initialRendererState = RendererBootstrapState.FullPrefix(ack, 1),
            acknowledgementObserver = { generation, sequence -> DurableAckCommit(sequence, generation) },
            acknowledgementResetObserver = { generation -> DurableAckCommit(0, generation + 1) },
        )
        val clientA = client(sessionA, 5)
        val clientB = client(sessionB, 9)
        try {
            clientA.start()
            clientB.start()
            socket.emitState(RealtimeState.Connected)
            withTimeout(2_000) {
                while (clientA.diagnostics().subscribeSendCount < 1 ||
                    clientB.diagnostics().subscribeSendCount < 1
                ) kotlinx.coroutines.yield()
            }

            socket.emitState(RealtimeState.Failed("physical relay failure"))
            runCurrent()
            multiplexer.onNetworkAvailable()
            runCurrent()
            assertEquals(5L, clientA.sessionState.value.lastAckedSequence)
            assertEquals(9L, clientB.sessionState.value.lastAckedSequence)
            socket.emitState(RealtimeState.Connected)
            withTimeout(2_000) {
                while (clientA.diagnostics().subscribeSendCount < 2 ||
                    clientB.diagnostics().subscribeSendCount < 2
                ) kotlinx.coroutines.yield()
            }

            assertEquals(listOf(5L, 5L, 9L, 9L), socket.sentEnvelopes
                .filter { it.messageType == MessageType.SUBSCRIBE }
                .map(Envelope::getAck)
                .sorted())
            assertEquals(5L, clientA.sessionState.value.lastAckedSequence)
            assertEquals(9L, clientB.sessionState.value.lastAckedSequence)
        } finally {
            clientA.close()
            clientB.close()
            multiplexer.close()
            androidIdentity.destroy()
            pcIdentity.destroy()
        }
    }

    @Test
    fun longForegroundProbesAndKeepsHealthySharedSocket(): Unit = runTest {
        val socket = FakeSocket()
        socket.livenessProbeSupported = true
        socket.nextLivenessProbe = CompletableDeferred(true)
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
        multiplexer.register(UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))
        endpointA.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        multiplexer.onAppForegrounded(14_999, networkUsable = true)
        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)

        multiplexer.onAppForegrounded(15_000, networkUsable = true)
        multiplexer.onAppForegrounded(30_000, networkUsable = true)
        runCurrent()

        assertEquals(1, socket.livenessProbeCount)
        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(2, endpointA.diagnostics().endpointCount)
        multiplexer.close()
    }

    @Test
    fun longForegroundKeepsHealthyLegacyRelayWithoutAuthenticatedProbe(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        multiplexer.onAppForegrounded(15_000, networkUsable = true)
        runCurrent()

        assertEquals(0, socket.livenessProbeCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun networkChangeCancelsPendingForegroundProbeAndReplacesOnlyOnce(): Unit = runTest {
        val socket = FakeSocket()
        socket.livenessProbeSupported = true
        socket.nextLivenessProbe = CompletableDeferred()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        multiplexer.onAppForegrounded(30_000, networkUsable = true)
        runCurrent()
        assertEquals(1, socket.livenessProbeCount)

        multiplexer.onNetworkAvailable()
        runCurrent()
        advanceTimeBy(ProductionDeviceConnectionSupervisor.FOREGROUND_LIVENESS_TIMEOUT_MILLIS)
        runCurrent()

        assertEquals(1, socket.disconnectCount)
        assertEquals(2, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun concurrentNetworkAndForegroundSignalsDoNotReplaceHealthyLegacyRelay(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()

        List(16) { index ->
            async(Dispatchers.Default) {
                if (index % 2 == 0) multiplexer.onNetworkAvailable()
                else multiplexer.onAppForegrounded(20_000, networkUsable = true)
            }
        }.awaitAll()
        runCurrent()

        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun foregroundDoesNotReviveWithoutEndpointOrUsableNetwork(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)

        multiplexer.onAppForegrounded(30_000, networkUsable = true)
        assertEquals(0, socket.connectCount)

        val endpoint = multiplexer.register(UUID.randomUUID())
        multiplexer.onAppForegrounded(30_000, networkUsable = false)
        assertEquals(0, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        endpoint.close()
        multiplexer.close()
    }

    @Test
    fun warmConnectAllocatesNoEndpointAndCoalescesUntilSocketSettles(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)

        multiplexer.warmConnect()
        multiplexer.warmConnect()
        assertEquals(1, socket.connectCount)
        val endpoint = multiplexer.register(UUID.randomUUID())
        assertEquals(1, endpoint.diagnostics().endpointCount)

        socket.emitState(RealtimeState.Connected)
        runCurrent()
        multiplexer.warmConnect()
        assertEquals(1, socket.connectCount)
        assertEquals(1, endpoint.diagnostics().endpointCount)
        multiplexer.close()
    }

    @Test
    fun shortForegroundReplacesStructurallyStaleConnectedSocket(): Unit = runTest {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(UUID.randomUUID())
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        socket.structurallyStale = true

        multiplexer.onAppForegrounded(1_000, networkUsable = true)

        assertEquals(1, socket.disconnectCount)
        assertEquals(2, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun foregroundProbeTimeoutReplacesAndFencesLateFramesFromOldGeneration(): Unit = runTest {
        val socket = FakeSocket()
        socket.livenessProbeSupported = true
        socket.nextLivenessProbe = CompletableDeferred()
        val sessionId = UUID.randomUUID()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(sessionId)

        multiplexer.onAppForegrounded(15_000, networkUsable = true)
        runCurrent()
        assertEquals(1, socket.livenessProbeCount)
        assertEquals(0, socket.disconnectCount)
        advanceTimeBy(ProductionDeviceConnectionSupervisor.FOREGROUND_LIVENESS_TIMEOUT_MILLIS)
        runCurrent()
        socket.deliver(socket.received(sessionId, sequence = 10, generation = 1))
        runCurrent()

        assertEquals(1, socket.releasedReservations.get())
        assertEquals(1, endpoint.diagnostics().routeDecisions.rejectedGeneration)
        multiplexer.close()
    }

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
    fun failedForcedRefreshRemainsTransientAndRetriesWithoutSignOut(): Unit = runTest {
        val socket = FakeSocket()
        val refreshCount = AtomicInteger(0)
        val failureCount = AtomicInteger(0)
        val multiplexer = ProductionRealtimeMultiplexer.forTest(
            socket = socket,
            parentScope = backgroundScope,
            authenticationRefresher = RealtimeAuthenticationRefresher {
                refreshCount.incrementAndGet()
                throw IllegalStateException("temporary refresh failure")
            },
            authenticationFailureObserver = { failureCount.incrementAndGet() },
            retryDelay = ProductionRealtimeRetryDelay { },
        )
        val endpoint = multiplexer.register(UUID.randomUUID())
        val failed = async { endpoint.state.first { it is RealtimeState.Failed } }

        endpoint.connect()
        socket.emitState(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()

        assertTrue(failed.await() is RealtimeState.Failed)
        assertEquals(1, refreshCount.get())
        assertEquals(0, failureCount.get())
        assertEquals(2, socket.connectCount)
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
            authenticationRefresher = RealtimeAuthenticationRefresher { },
        )
        val failedEndpoint = failedMultiplexer.register(UUID.randomUUID())
        val authenticationFailed = async {
            failedEndpoint.state.first { it is RealtimeState.AuthenticationFailed }
        }
        failedEndpoint.connect()
        failedSocket.emitState(RealtimeState.AuthenticationRejected(401, "expired"))
        runCurrent()
        failedSocket.emitState(RealtimeState.AuthenticationRejected(403, "still rejected"))
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
                    }

                    override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
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
            withTimeout(5_000) { endpointA.state.first { it == RealtimeState.Connected } }
            endpointA.sendSubscribe(sessionA, senderId = androidId)
            endpointB.sendSubscribe(sessionB, senderId = androidId)

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
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(sessionA)
        endpointB.sendSubscribe(sessionB)
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
        assertEquals(0, socket.disconnectCount)

        endpointB.close()
        assertEquals(0, socket.closeCount)
        multiplexer.close()
        assertEquals(1, socket.closeCount)
    }

    @Test
    fun endpointRendererFailureDoesNotDisconnectPeerOrPhysicalSocket(): Unit = runTest {
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)
        endpointA.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(sessionA)
        endpointB.sendSubscribe(sessionB)

        endpointA.disconnect("renderer failed")
        assertTrue(!endpointA.disconnectIfCurrentGeneration(1, "renderer failed"))
        val peerFrames = async {
            endpointB.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionB, 11, generation = 1))
        socket.deliver(socket.received(sessionB, 12, generation = 1))
        runCurrent()

        assertEquals(listOf(11L, 12L), peerFrames.await())
        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.connectCount)
        multiplexer.close()
    }

    @Test
    fun routerReleasesSocketReservationBeforeSlowEndpointConsumesFrame(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)

        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(sessionId)
        val generation = socket.diagnostics().connectedGeneration

        socket.deliver(socket.received(sessionId, sequence = 1, generation = generation))
        runCurrent()

        assertEquals(1, socket.releasedReservations.get())
        assertEquals(
            ProductionReceivePressure(generation, false, 1, 64),
            endpoint.receivePressure.value,
        )

        val received = endpoint.receivedEnvelopes.first()
        assertEquals(1L, received.envelope.seq)
        assertEquals(1, socket.releasedReservations.get())
        assertEquals(1, endpoint.receivePressure.value.bufferedEvents)

        received.close()
        received.close()
        assertEquals(ProductionReceivePressure(generation, false, 0, 0), endpoint.receivePressure.value)
        assertEquals(1, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun slowEndpointHighWatermarkDoesNotPausePeerOrReplaceSharedSocket(): Unit = runTest {
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        socket.livenessProbeSupported = true
        socket.nextLivenessProbe = CompletableDeferred(true)
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)

        endpointA.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(sessionA)
        endpointB.sendSubscribe(sessionB)
        val generation = socket.diagnostics().connectedGeneration

        repeat(ProductionRealtimeClient.HIGH_BUFFERED_EVENTS) { index ->
            socket.deliver(
                socket.received(
                    sessionA,
                    sequence = index.toLong() + 1,
                    generation = generation,
                    wireBytes = 64,
                ),
            )
        }
        runCurrent()

        assertEquals(
            ProductionReceivePressure(
                generation = generation,
                paused = true,
                bufferedEvents = ProductionRealtimeClient.HIGH_BUFFERED_EVENTS,
                bufferedWireBytes = ProductionRealtimeClient.HIGH_BUFFERED_EVENTS * 64L,
            ),
            endpointA.receivePressure.value,
        )
        assertEquals(ProductionReceivePressure(0, false, 0, 0), endpointB.receivePressure.value)
        assertEquals(ProductionRealtimeClient.HIGH_BUFFERED_EVENTS, socket.releasedReservations.get())
        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(generation, socket.diagnostics().generation)
        assertEquals(generation, socket.diagnostics().connectedGeneration)

        val peerFrame = async {
            endpointB.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionB, sequence = 900, generation = generation, wireBytes = 64))
        runCurrent()

        assertEquals(listOf(900L), peerFrame.await())
        assertEquals(ProductionReceivePressure(generation, true, ProductionRealtimeClient.HIGH_BUFFERED_EVENTS,
            ProductionRealtimeClient.HIGH_BUFFERED_EVENTS * 64L), endpointA.receivePressure.value)
        assertEquals(ProductionReceivePressure(generation, false, 0, 0), endpointB.receivePressure.value)
        assertEquals(ProductionRealtimeClient.HIGH_BUFFERED_EVENTS + 1, socket.releasedReservations.get())
        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(generation, socket.diagnostics().generation)
        assertEquals(generation, socket.diagnostics().connectedGeneration)

        multiplexer.onAppForegrounded(
            ProductionDeviceConnectionSupervisor.FOREGROUND_RECONNECT_THRESHOLD_MILLIS,
            networkUsable = true,
        )
        runCurrent()
        assertEquals(1, socket.livenessProbeCount)
        assertEquals(1, socket.connectCount)
        assertEquals(0, socket.disconnectCount)
        assertEquals(generation, socket.diagnostics().connectedGeneration)

        endpointA.close()
        endpointB.close()
        assertEquals(ProductionReceivePressure(generation, false, 0, 0), endpointA.receivePressure.value)
        multiplexer.close()
        assertEquals(ProductionRealtimeClient.HIGH_BUFFERED_EVENTS + 1, socket.releasedReservations.get())
    }

    @Test
    fun endpointHardWireLimitReplacesGenerationAndCloseReleasesQuotaExactlyOnce(): Unit = runTest {
        val sessionA = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val sessionB = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpointA = multiplexer.register(sessionA)
        val endpointB = multiplexer.register(sessionB)
        val wireBytes = (ProductionRealtimeClient.HARD_BUFFERED_WIRE_BYTES / 8L).toInt()

        endpointA.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(sessionA)
        endpointB.sendSubscribe(sessionB)
        val generation = socket.diagnostics().connectedGeneration

        repeat(8) { index ->
            socket.deliver(
                socket.received(
                    sessionA,
                    sequence = index.toLong() + 1,
                    generation = generation,
                    wireBytes = wireBytes,
                ),
            )
        }
        socket.deliver(
            socket.received(
                sessionA,
                sequence = 9,
                generation = generation,
                wireBytes = wireBytes,
            ),
        )
        runCurrent()

        assertEquals(
            ProductionReceivePressure(
                generation = generation,
                paused = true,
                bufferedEvents = 8,
                bufferedWireBytes = ProductionRealtimeClient.HARD_BUFFERED_WIRE_BYTES,
            ),
            endpointA.receivePressure.value,
        )
        assertEquals(1L, endpointA.diagnostics().routeDecisions.overflow)
        assertEquals(9, socket.releasedReservations.get())
        assertEquals(ProductionReceivePressure(0, false, 0, 0), endpointB.receivePressure.value)
        assertEquals(2, socket.connectCount)
        assertEquals(1, socket.disconnectCount)
        assertTrue(socket.diagnostics().generation > generation)
        assertEquals(0, socket.diagnostics().connectedGeneration)

        socket.deliver(socket.received(sessionB, sequence = 900, generation = generation, wireBytes = 64))
        runCurrent()
        assertEquals(10, socket.releasedReservations.get())
        assertTrue(endpointB.diagnostics().routeDecisions.rejectedGeneration >= 1L)

        socket.emitState(RealtimeState.Connected)
        runCurrent()
        val successorGeneration = socket.diagnostics().connectedGeneration
        endpointA.sendSubscribe(sessionA)
        endpointB.sendSubscribe(sessionB)
        val peerFrame = async {
            endpointB.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionB, sequence = 901, generation = successorGeneration, wireBytes = 64))
        runCurrent()
        assertEquals(listOf(901L), peerFrame.await())
        assertEquals(11, socket.releasedReservations.get())
        assertEquals(ProductionReceivePressure(generation, true, 8,
            ProductionRealtimeClient.HARD_BUFFERED_WIRE_BYTES), endpointA.receivePressure.value)
        assertEquals(ProductionReceivePressure(successorGeneration, false, 0, 0), endpointB.receivePressure.value)

        endpointA.close()
        assertEquals(ProductionReceivePressure(generation, false, 0, 0), endpointA.receivePressure.value)
        endpointA.close()
        assertEquals(ProductionReceivePressure(generation, false, 0, 0), endpointA.receivePressure.value)
        assertEquals(11, socket.releasedReservations.get())
        multiplexer.close()
        assertEquals(11, socket.releasedReservations.get())
    }

    @Test
    fun endpointOverflowReplacesGenerationAndReplaysFromFreshSubscription(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val peerSessionId = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(sessionId)
        val peer = multiplexer.register(peerSessionId)

        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(sessionId)
        peer.sendSubscribe(peerSessionId)
        repeat(ProductionRealtimeClient.MAX_BUFFERED_EVENTS + 2) { index ->
            socket.deliver(socket.received(sessionId, index.toLong() + 1, generation = 1))
        }
        runCurrent()

        assertEquals(1, socket.disconnectCount)
        assertEquals(2, socket.connectCount)
        assertEquals(ProductionRealtimeClient.MAX_BUFFERED_EVENTS + 2, socket.releasedReservations.get())
        assertEquals(1L, endpoint.diagnostics().routeDecisions.overflow)
        assertEquals(1L, endpoint.diagnostics().routeDecisions.rejectedGeneration)
        socket.deliver(socket.received(peerSessionId, 900, generation = 1))
        runCurrent()
        assertTrue(endpoint.diagnostics().routeDecisions.rejectedGeneration >= 2L)
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

        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(sessionId)
        peer.sendSubscribe(peerSessionId)
        val successorGeneration = socket.diagnostics().connectedGeneration
        val nextGeneration = async {
            endpoint.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        val peerFrame = async {
            peer.receivedEnvelopes.take(1).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 129, generation = successorGeneration))
        socket.deliver(socket.received(peerSessionId, 901, generation = successorGeneration))
        runCurrent()
        assertEquals(listOf(129L), nextGeneration.await())
        assertEquals(listOf(901L), peerFrame.await())
        assertEquals(ProductionRealtimeClient.MAX_BUFFERED_EVENTS + 5, socket.releasedReservations.get())

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

        val missingSession = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val liveSession = UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val endpoint = multiplexer.register(liveSession)
        endpoint.connect()
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpoint.sendSubscribe(liveSession)

        val admitted = async {
            endpoint.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(missingSession, 305, generation = 1))
        socket.deliver(socket.received(liveSession, 306, generation = 1))
        socket.deliver(socket.received(liveSession, 307, generation = 1))
        runCurrent()
        assertEquals(listOf(306L, 307L), admitted.await())
        assertEquals(1L, endpoint.diagnostics().routeDecisions.noEndpoint)
        assertEquals(0L, endpoint.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(0, socket.disconnectCount)
        assertEquals(3, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closedEndpointIsolatedWithoutPoisoningReplacementOrSocket(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldEndpoint = multiplexer.register(sessionId)
        oldEndpoint.close()

        socket.deliver(socket.received(sessionId, 306, generation = 1))
        runCurrent()
        val replacement = multiplexer.register(sessionId)
        socket.activateGeneration(1)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        replacement.sendSubscribe(sessionId)

        val replay = async {
            replacement.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 1))
        socket.deliver(socket.received(sessionId, 307, generation = 1))
        runCurrent()

        assertEquals(listOf(306L, 307L), replay.await())
        assertEquals(1L, replacement.diagnostics().routeDecisions.endpointClosed)
        assertEquals(0L, replacement.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(0, socket.disconnectCount)
        assertEquals(3, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closingBufferedEndpointReleasesFramesWithoutDisconnectingReplacement(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldEndpoint = multiplexer.register(sessionId)
        socket.activateGeneration(2)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        oldEndpoint.sendSubscribe(sessionId)

        socket.deliver(socket.received(sessionId, 306, generation = 2))
        runCurrent()
        assertEquals(1, socket.releasedReservations.get())

        oldEndpoint.close()
        assertEquals(1, socket.releasedReservations.get())
        assertEquals(0, socket.disconnectCount)

        val replacement = multiplexer.register(sessionId)
        replacement.sendSubscribe(sessionId)

        val replay = async {
            replacement.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 2))
        socket.deliver(socket.received(sessionId, 307, generation = 2))
        runCurrent()

        assertEquals(listOf(306L, 307L), replay.await())
        assertEquals(0L, replacement.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(3, socket.releasedReservations.get())
        multiplexer.close()
    }

    @Test
    fun closingEndpointWithInFlightFrameKeepsSharedSocketAndReplacementAlive(): Unit = runTest {
        val sessionId = UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, backgroundScope)
        val oldEndpoint = multiplexer.register(sessionId)
        socket.activateGeneration(2)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        oldEndpoint.sendSubscribe(sessionId)
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
        assertEquals(1, socket.releasedReservations.get())

        oldEndpoint.close()
        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.releasedReservations.get())

        val replacement = multiplexer.register(sessionId)
        replacement.sendSubscribe(sessionId)
        allowRelease.complete(Unit)
        collector.join()

        val replay = async {
            replacement.receivedEnvelopes.take(2).map { it.use { item -> item.envelope.seq } }.toList()
        }
        socket.deliver(socket.received(sessionId, 306, generation = 2))
        socket.deliver(socket.received(sessionId, 307, generation = 2))
        runCurrent()

        assertEquals(listOf(306L, 307L), replay.await())
        assertEquals(0L, replacement.diagnostics().routeDecisions.rejectedGeneration)
        assertEquals(3, socket.releasedReservations.get())
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
        socket.activateGeneration(2)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(sessionA)
        replacementB.sendSubscribe(sessionB)

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
        socket.activateGeneration(1)
        socket.emitState(RealtimeState.Connected)
        runCurrent()
        endpointA.sendSubscribe(UUID.fromString("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))
        endpointB.sendSubscribe(UUID.fromString("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))

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
        assertEquals(7, socket.releasedReservations.get())

        endpointA.close()
        assertEquals(7, socket.releasedReservations.get())
        endpointB.diagnostics()
        multiplexer.close()
        assertEquals(7, socket.releasedReservations.get())
    }

    @Test
    fun networkAvailableConnectsFailedSocketButKeepsSettledSocket(): Unit = runTest {
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

            assertEquals(
                "phase=$phase",
                0,
                socket.disconnectCount,
            )
            assertEquals(
                "phase=$phase",
                if (phase is RealtimeState.Failed) 2 else 1,
                socket.connectCount,
            )
            multiplexer.close()
        }
    }

    @Test
    fun networkAvailableWithMultipleEndpointsKeepsHealthyPhysicalSocket(): Unit = runTest {
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

        assertEquals(0, socket.disconnectCount)
        assertEquals(1, socket.connectCount)
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

    private fun ProductionRealtimeSessionTransport.sendSubscribe(
        sessionId: UUID,
        ack: Long = 0,
        senderId: UUID = UUID(0, 0),
    ) {
        assertTrue(
            send(
                Envelope.newBuilder()
                    .setProtocolVersion(1)
                    .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
                    .setSenderDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(senderId)))
                    .setDirection(Direction.ANDROID_TO_PC)
                    .setMessageType(MessageType.SUBSCRIBE)
                    .setAck(ack)
                    .build(),
            ),
        )
    }

    private fun ProductionRealtimeSessionTransport.sendUnsubscribe(sessionId: UUID) {
        assertTrue(
            send(
                Envelope.newBuilder()
                    .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
                    .setMessageType(MessageType.UNSUBSCRIBE)
                    .build(),
            ),
        )
    }

    private fun FakeSocket.received(
        sessionId: UUID,
        sequence: Long,
        generation: Int = 1,
        wireBytes: Int = 64,
    ): ProductionReceivedEnvelope =
        ProductionReceivedEnvelope(
            envelope = Envelope.newBuilder()
                .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
                .setSeq(sequence)
                .build(),
            wireBytes = wireBytes,
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
        override val negotiatedCapabilities = MutableStateFlow(ProductionRealtimeNegotiatedCapabilities(0, emptySet()))
        val incoming = Channel<ProductionReceivedEnvelope>(Channel.UNLIMITED)
        val releasedReservations = AtomicInteger(0)
        private val mutableState = MutableSharedFlow<RealtimeState>(extraBufferCapacity = 8)
        private val mutablePressure = MutableStateFlow(ProductionReceivePressure(0, false, 0, 0))
        private val connects = AtomicInteger(0)
        private val disconnects = AtomicInteger(0)
        private val closes = AtomicInteger(0)
        private val livenessProbes = AtomicInteger(0)
        val connectCount: Int get() = connects.get()
        val disconnectCount: Int get() = disconnects.get()
        val closeCount: Int get() = closes.get()
        val livenessProbeCount: Int get() = livenessProbes.get()
        @Volatile var structurallyStale = false
        @Volatile var livenessProbeSupported = false
        @Volatile var nextLivenessProbe: CompletableDeferred<Boolean>? = null
        @Volatile private var diagnosticsGeneration = 0
        @Volatile private var diagnosticsConnectedGeneration = 0
        @Volatile private var diagnosticsState = "IDLE"
        @Volatile private var connectEntered: CountDownLatch? = null
        @Volatile private var connectRelease: CountDownLatch? = null
        @Volatile private var sendEntered: CountDownLatch? = null
        @Volatile private var sendRelease: CountDownLatch? = null
        val sentEnvelopes = CopyOnWriteArrayList<Envelope>()
        val sentExactBytes = CopyOnWriteArrayList<ByteArray>()

        override val receivedEnvelopes = incoming.receiveAsFlow()
        override val receivePressure: StateFlow<ProductionReceivePressure> = mutablePressure
        override val state: SharedFlow<RealtimeState> = mutableState

        override fun connect() {
            diagnosticsGeneration++
            diagnosticsConnectedGeneration = 0
            diagnosticsState = "CONNECTING"
            connects.incrementAndGet()
            connectEntered?.also {
                connectEntered = null
                it.countDown()
                connectRelease?.await(2, TimeUnit.SECONDS)
                connectRelease = null
            }
            mutableState.tryEmit(RealtimeState.Connecting)
        }

        fun blockNextConnect(entered: CountDownLatch, release: CountDownLatch) {
            connectEntered = entered
            connectRelease = release
        }

        fun blockNextSend(entered: CountDownLatch, release: CountDownLatch) {
            sendEntered = entered
            sendRelease = release
        }

        fun activateGeneration(generation: Int) {
            diagnosticsGeneration = generation
            diagnosticsConnectedGeneration = generation
            diagnosticsState = "CONNECTED"
        }

        override fun send(envelope: Envelope): Boolean {
            sendEntered?.also {
                sendEntered = null
                it.countDown()
                sendRelease?.await(2, TimeUnit.SECONDS)
                sendRelease = null
            }
            sentEnvelopes += envelope
            return true
        }

        override fun sendExact(envelope: Envelope, exactBytes: ByteArray): Boolean {
            sentExactBytes += exactBytes.copyOf()
            return send(envelope)
        }

        override fun supportsLivenessProbe(expectedGeneration: Int): Boolean =
            livenessProbeSupported && expectedGeneration > 0 &&
                diagnosticsConnectedGeneration == expectedGeneration &&
                diagnosticsGeneration == expectedGeneration && diagnosticsState == "CONNECTED"

        override suspend fun probeLiveness(expectedGeneration: Int, timeoutMillis: Long): Boolean {
            if (!supportsLivenessProbe(expectedGeneration)) return false
            livenessProbes.incrementAndGet()
            val result = nextLivenessProbe ?: return false
            return withTimeoutOrNull(timeoutMillis) { result.await() } == true &&
                supportsLivenessProbe(expectedGeneration)
        }

        override fun disconnect(reason: String) {
            diagnosticsGeneration++
            diagnosticsConnectedGeneration = 0
            diagnosticsState = "FAILED"
            disconnects.incrementAndGet()
            mutableState.tryEmit(RealtimeState.Failed(reason))
        }

        override fun close() {
            closes.incrementAndGet()
            diagnosticsState = "CLOSED"
            incoming.close()
        }

        fun emitState(state: RealtimeState) {
            diagnosticsState = when (state) {
                RealtimeState.Connecting -> "CONNECTING"
                RealtimeState.Connected -> {
                    diagnosticsConnectedGeneration = diagnosticsGeneration
                    "CONNECTED"
                }
                is RealtimeState.Failed -> "FAILED"
                is RealtimeState.AuthenticationFailed -> "AUTHENTICATION_FAILED"
                is RealtimeState.AuthenticationRejected -> "AUTHENTICATION_REJECTED"
                is RealtimeState.Reconnecting -> "RECONNECTING"
                RealtimeState.Closed -> "CLOSED"
            }
            check(mutableState.tryEmit(state))
        }

        override fun diagnostics(): ProductionRealtimeSocketDiagnostics = ProductionRealtimeSocketDiagnostics(
            generation = diagnosticsGeneration,
            connectedGeneration = diagnosticsConnectedGeneration,
            socketPresent = diagnosticsState == "CONNECTING" ||
                (diagnosticsState == "CONNECTED" && !structurallyStale),
            state = diagnosticsState,
            bufferedEvents = 0,
            bufferedWireBytes = 0,
        )
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

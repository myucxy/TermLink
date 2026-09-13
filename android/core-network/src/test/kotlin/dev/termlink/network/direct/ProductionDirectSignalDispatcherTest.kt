package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.util.ArrayDeque
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout

class ProductionDirectSignalDispatcherTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val closeables = ArrayList<AutoCloseable>()

    @AfterTest
    fun tearDown() {
        closeables.asReversed().forEach { runCatching(it::close) }
        scope.cancel()
    }

    @Test
    fun oneReaderSplitsDiscoveryAndDataInOrderAndStartIsIdempotent(): Unit = runBlocking {
        val fixture = fixture()
        val discovery = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        val data = assertNotNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.installPromotedPathOwnerResolver { it.signalSeq == 4L })
        val discovered = async { discovery.incoming.take(4).toList() }
        val cutovers = async { data.incoming.take(3).toList() }

        assertTrue(discovery.start())
        assertTrue(fixture.dispatcher.start())
        assertTrue(discovery.start())
        withTimeout(2_000) {
            while (fixture.client.collectionStarts.get() != 1) delay(1)
        }
        listOf(1, 6, 5, 10, 11, 9, 14).forEachIndexed { index, type ->
            fixture.client.emit(signal(type, index + 1))
        }

        assertEquals(listOf(1, 5, 11, 14), withTimeout(2_000) { discovered.await() }.map { it.signalType.number })
        assertEquals(listOf(6, 10, 9), withTimeout(2_000) { cutovers.await() }.map { it.signalType.number })
        assertEquals(1, fixture.client.startCalls.get())
        assertEquals(1, fixture.client.collectionStarts.get())
        assertIs<ProductionDirectSignalDispatcherStatus.RUNNING>(fixture.dispatcher.status.value)
    }

    @Test
    fun pathCloseWithoutResolverDefaultsOnlyToDiscoveryAndCannotInstallAfterStart(): Unit = runBlocking {
        val fixture = fixture()
        val discovery = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        assertNotNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.start())
        awaitReader(fixture)
        val received = async(start = CoroutineStart.UNDISPATCHED) { discovery.incoming.first() }

        fixture.client.emit(signal(10, 1))

        assertEquals(1L, withTimeout(2_000) { received.await() }.signalSeq)
        assertEquals(0, fixture.dispatcher.diagnostics().dataQueuedFrames)
        assertFalse(fixture.dispatcher.installPromotedPathOwnerResolver { true })
    }

    @Test
    fun pathCloseRoutesToExactlyOneOwnerBeforeAndAfterPromotion(): Unit = runBlocking {
        val fixture = fixture()
        val discovery = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        val data = assertNotNull(fixture.dispatcher.acquireDataLane())
        val promotedSequences = setOf(2L, 3L)
        assertTrue(
            fixture.dispatcher.installPromotedPathOwnerResolver { envelope ->
                when (envelope.signalSeq) {
                    3L -> error("A failed ownership lookup must remain pre-promotion-owned")
                    else -> envelope.signalSeq in promotedSequences
                }
            },
        )
        assertFalse(fixture.dispatcher.installPromotedPathOwnerResolver { true })
        assertTrue(fixture.dispatcher.start())
        awaitReader(fixture)
        val discoveryFrames = async(start = CoroutineStart.UNDISPATCHED) {
            discovery.incoming.take(2).toList()
        }
        val dataFrames = async(start = CoroutineStart.UNDISPATCHED) {
            data.incoming.take(1).toList()
        }

        fixture.client.emit(signal(10, 1))
        fixture.client.emit(signal(10, 2))
        fixture.client.emit(signal(10, 3))

        assertEquals(
            listOf(1L, 3L),
            withTimeout(2_000) { discoveryFrames.await() }.map { it.signalSeq },
        )
        assertEquals(
            listOf(2L),
            withTimeout(2_000) { dataFrames.await() }.map { it.signalSeq },
        )
        assertEquals(0, fixture.dispatcher.diagnostics().discoveryDroppedFrames)
        assertEquals(0, fixture.dispatcher.diagnostics().dataDroppedFrames)
    }

    @Test
    fun discoveryFrameOverflowDropsLocallyAndDataLaneContinues(): Unit = runBlocking {
        val fixture = fixture(maximumFrames = 2, maximumBytes = 1_024)
        val observedFault = AtomicReference<ProductionDirectSignalLaneFault?>(null)
        assertTrue(fixture.dispatcher.installLaneFaultObserver(observedFault::set))
        assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        val data = assertNotNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.start())
        awaitReader(fixture)

        listOf(signal(1, 1), signal(2, 2), signal(3, 3)).forEach { fixture.client.emit(it) }
        withTimeout(2_000) {
            while (fixture.dispatcher.latestLaneFault.value == null) delay(1)
        }

        val fault = assertNotNull(fixture.dispatcher.latestLaneFault.value)
        assertEquals(ProductionDirectSignalLaneKind.DISCOVERY, fault.lane)
        assertEquals(DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW, fault.code)
        assertEquals(SIGNAL_SENDER, fault.senderDeviceId)
        assertEquals(SIGNAL_CONNECTION, fault.connectionId)
        assertEquals(7, fault.pathEpoch)
        assertEquals(fault, observedFault.get())
        assertIs<ProductionDirectSignalDispatcherStatus.RUNNING>(fixture.dispatcher.status.value)
        assertEquals(0, fixture.client.closeCalls.get())
        assertEquals(0, fixture.relayTouches.get(), "Lane pressure must have no Relay owner to touch")
        val received = async { data.incoming.first() }
        fixture.client.emit(signal(6, 4))
        assertEquals(6, withTimeout(2_000) { received.await() }.signalType.number)
        fixture.dispatcher.diagnostics().let { diagnostics ->
            assertFalse(diagnostics.closed)
            assertEquals(2, diagnostics.discoveryQueuedFrames)
            assertEquals(1, diagnostics.discoveryDroppedFrames)
            assertEquals(0, diagnostics.dataDroppedFrames)
        }
    }

    @Test
    fun dataByteOverflowDropsLocallyAndDiscoveryLaneContinues(): Unit = runBlocking {
        val firstDataSignal = signal(6, 1, payloadBytes = 40)
        val secondDataSignal = signal(7, 2, payloadBytes = 40)
        val fixture = fixture(
            maximumFrames = 4,
            maximumBytes = firstDataSignal.serializedSize.toLong() +
                secondDataSignal.serializedSize.toLong() - 1L,
        )
        val discovery = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        assertNotNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.start())
        awaitReader(fixture)

        fixture.client.emit(firstDataSignal)
        fixture.client.emit(secondDataSignal)
        withTimeout(2_000) {
            while (fixture.dispatcher.latestLaneFault.value == null) delay(1)
        }

        val fault = assertNotNull(fixture.dispatcher.latestLaneFault.value)
        assertEquals(ProductionDirectSignalLaneKind.DATA, fault.lane)
        assertEquals(1, fault.totalDroppedFrames)
        assertEquals(secondDataSignal.serializedSize.toLong(), fault.droppedFrameBytes)
        assertEquals(0, fixture.client.closeCalls.get())
        val received = async { discovery.incoming.first() }
        fixture.client.emit(signal(1, 3))
        assertEquals(1, withTimeout(2_000) { received.await() }.signalType.number)
        assertIs<ProductionDirectSignalDispatcherStatus.RUNNING>(fixture.dispatcher.status.value)
    }

    @Test
    fun closingOneLeaseNeverClosesTheSharedClientAndTheLaneCanBeReacquired(): Unit = runBlocking {
        val fixture = fixture()
        val discovery = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        val data = assertNotNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.installPromotedPathOwnerResolver { true })
        assertNull(fixture.dispatcher.acquireDiscoveryLane())
        assertNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.start())

        discovery.close()
        assertEquals(0, fixture.client.closeCalls.get())
        assertFalse(discovery.start())
        assertFalse(discovery.multipairV2Active)
        assertIs<DirectSignalingCallResult.Fault>(discovery.fetchConfig())
        assertIs<DirectSignalingCallResult.Fault>(
            discovery.issueTicket(UUID.randomUUID(), UUID.randomUUID(), config(permitted = false)),
        )
        assertFalse(discovery.send(signal(1, 1)))
        assertEquals(0, fixture.client.fetchCalls.get())
        assertEquals(0, fixture.client.issueTicketCalls.get())
        assertEquals(0, fixture.client.sendCalls.get())
        val receivedData = async { data.incoming.first() }
        fixture.client.emit(signal(10, 1))
        assertEquals(10, withTimeout(2_000) { receivedData.await() }.signalType.number)

        val replacement = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        val receivedDiscovery = async { replacement.incoming.first() }
        fixture.client.emit(signal(11, 2))
        assertEquals(11, withTimeout(2_000) { receivedDiscovery.await() }.signalType.number)
        val sentBeforeClose = fixture.client.sendCalls.get()
        replacement.close()
        data.close()
        assertFalse(data.send(signal(6, 3)))
        assertEquals(sentBeforeClose, fixture.client.sendCalls.get())
        assertEquals(0, fixture.client.closeCalls.get())
    }

    @Test
    fun refreshRoutingPermissionNeedsNoDiscoveryLeaseAndDistinguishesAllStates(): Unit = runBlocking {
        val fixture = fixture()
        fixture.client.configs += DirectSignalingCallResult.Fault(
            DirectSignalingFault(DirectSignalingFaultCode.HTTP_UNAVAILABLE),
        )
        fixture.client.configs += DirectSignalingCallResult.Success(config(permitted = false))
        fixture.client.configs += DirectSignalingCallResult.Success(config(permitted = true))

        assertEquals(ProductionDirectRoutingPermission.UNKNOWN, fixture.dispatcher.refreshRoutingPermission())
        assertEquals(ProductionDirectRoutingPermission.FORBIDDEN, fixture.dispatcher.refreshRoutingPermission())
        assertEquals(ProductionDirectRoutingPermission.PERMITTED, fixture.dispatcher.refreshRoutingPermission())
        assertFalse(fixture.dispatcher.diagnostics().discoveryLeaseActive)
        assertFalse(fixture.dispatcher.diagnostics().dataLeaseActive)
        assertEquals(3, fixture.client.fetchCalls.get())
    }

    @Test
    fun concurrentStartNeverReportsAProvisionalFailureAsSuccessAndCanRetry(): Unit = runBlocking {
        val fixture = fixture()
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        fixture.client.firstStartEntered = entered
        fixture.client.releaseFirstStart = release
        fixture.client.startResults += false
        fixture.client.startResults += false
        fixture.client.startResults += true

        val first = async(Dispatchers.Default) { fixture.dispatcher.start() }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val second = async(Dispatchers.Default) { fixture.dispatcher.start() }
        delay(25)
        assertFalse(second.isCompleted)
        release.countDown()

        assertFalse(withTimeout(2_000) { first.await() })
        assertFalse(withTimeout(2_000) { second.await() })
        assertEquals(2, fixture.client.startCalls.get())
        assertEquals(0, fixture.client.collectionStarts.get())
        assertTrue(fixture.dispatcher.start())
        awaitReader(fixture)
        assertEquals(3, fixture.client.startCalls.get())
        assertEquals(1, fixture.client.collectionStarts.get())
    }

    @Test
    fun terminalClientFaultIsObservedWithoutClosingSharedClientOrRelay(): Unit = runBlocking {
        val fixture = fixture()
        val discovery = assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        val data = assertNotNull(fixture.dispatcher.acquireDataLane())
        fixture.client.configs += DirectSignalingCallResult.Success(config(permitted = true))
        assertEquals(ProductionDirectRoutingPermission.PERMITTED, fixture.dispatcher.refreshRoutingPermission())
        assertTrue(fixture.dispatcher.start())
        awaitReader(fixture)

        fixture.client.publishState(
            DirectTransportSignalingState.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.HTTP_UNAVAILABLE),
                willRetry = true,
                attempt = 1,
            ),
        )
        delay(25)
        assertIs<ProductionDirectSignalDispatcherStatus.RUNNING>(fixture.dispatcher.status.value)
        fixture.client.publishState(
            DirectTransportSignalingState.Fault(
                DirectSignalingFault(DirectSignalingFaultCode.RETRY_EXHAUSTED),
                willRetry = false,
                attempt = 3,
            ),
        )
        withTimeout(2_000) {
            while (fixture.dispatcher.status.value !is ProductionDirectSignalDispatcherStatus.FAULT) delay(1)
        }

        val status = assertIs<ProductionDirectSignalDispatcherStatus.FAULT>(fixture.dispatcher.status.value)
        assertEquals(DirectSignalingFaultCode.RETRY_EXHAUSTED, status.code)
        assertEquals(ProductionDirectRoutingPermission.UNKNOWN, fixture.dispatcher.routingPermission.value)
        assertFalse(discovery.start())
        assertFalse(discovery.send(signal(1, 1)))
        assertFalse(data.send(signal(6, 1)))
        assertEquals(0, fixture.client.sendCalls.get())
        assertEquals(0, fixture.client.closeCalls.get())
        assertEquals(0, fixture.relayTouches.get())
        discovery.close()
        data.close()
        assertNull(fixture.dispatcher.acquireDiscoveryLane())
        assertNull(fixture.dispatcher.acquireDataLane())
    }

    @Test
    fun dispatcherCloseCancelsBothQueuesClearsCountersAndClosesClientOnce(): Unit = runBlocking {
        val fixture = fixture()
        assertNotNull(fixture.dispatcher.acquireDiscoveryLane())
        assertNotNull(fixture.dispatcher.acquireDataLane())
        assertTrue(fixture.dispatcher.start())
        withTimeout(2_000) {
            while (fixture.client.collectionStarts.get() != 1) delay(1)
        }
        fixture.client.emit(signal(1, 1, payloadBytes = 64))
        fixture.client.emit(signal(6, 2, payloadBytes = 64))
        withTimeout(2_000) {
            while (fixture.dispatcher.diagnostics().run {
                    discoveryQueuedFrames != 1 || dataQueuedFrames != 1
                }
            ) delay(1)
        }

        fixture.dispatcher.closeAndJoin()
        fixture.dispatcher.close()

        assertEquals(1, fixture.client.closeCalls.get())
        assertFalse(fixture.dispatcher.start())
        assertNull(fixture.dispatcher.acquireDiscoveryLane())
        assertNull(fixture.dispatcher.acquireDataLane())
        assertEquals(ProductionDirectRoutingPermission.UNKNOWN, fixture.dispatcher.routingPermission.value)
        assertIs<ProductionDirectSignalDispatcherStatus.CLOSED>(fixture.dispatcher.status.value)
        val diagnostics = fixture.dispatcher.diagnostics()
        assertTrue(diagnostics.closed)
        assertFalse(diagnostics.readerActive)
        assertFalse(diagnostics.discoveryLeaseActive)
        assertFalse(diagnostics.dataLeaseActive)
        assertEquals(0, diagnostics.discoveryQueuedFrames)
        assertEquals(0, diagnostics.discoveryQueuedBytes)
        assertEquals(0, diagnostics.dataQueuedFrames)
        assertEquals(0, diagnostics.dataQueuedBytes)
    }

    private fun fixture(
        maximumFrames: Int = ProductionDirectSignalDispatcher.MAXIMUM_FRAMES_PER_LANE,
        maximumBytes: Long = ProductionDirectSignalDispatcher.MAXIMUM_BYTES_PER_LANE,
    ): Fixture {
        val client = FakeClient()
        val relayTouches = AtomicInteger(0)
        val dispatcher = ProductionDirectSignalDispatcher(
            client,
            scope,
            maximumFramesPerLane = maximumFrames,
            maximumBytesPerLane = maximumBytes,
        )
        closeables += dispatcher
        return Fixture(dispatcher, client, relayTouches)
    }

    private suspend fun awaitReader(fixture: Fixture) {
        withTimeout(2_000) {
            while (fixture.client.collectionStarts.get() != 1) delay(1)
        }
    }

    private data class Fixture(
        val dispatcher: ProductionDirectSignalDispatcher,
        val client: FakeClient,
        val relayTouches: AtomicInteger,
    )

    private class FakeClient : ProductionDirectSignalClientBoundary {
        private val source = MutableSharedFlow<TransportSignalEnvelopeV1>(extraBufferCapacity = 128)
        private val mutableState = MutableStateFlow<DirectTransportSignalingState>(
            DirectTransportSignalingState.Idle,
        )
        private val mutableCapabilities = MutableStateFlow(
            DirectTransportSignalingNegotiatedCapabilities(
                7,
                setOf(
                    DirectTransportSignalingClient.DIRECT_SIGNALING_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_SHADOW_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY,
                    DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY,
                ),
            ),
        )
        val collectionStarts = AtomicInteger(0)
        val startCalls = AtomicInteger(0)
        val closeCalls = AtomicInteger(0)
        val fetchCalls = AtomicInteger(0)
        val issueTicketCalls = AtomicInteger(0)
        val sendCalls = AtomicInteger(0)
        val configs = ArrayDeque<DirectSignalingCallResult<DirectTransportServerConfig>>()
        val startResults = ArrayDeque<Boolean>()
        val sent = ArrayList<TransportSignalEnvelopeV1>()
        val closed = AtomicBoolean(false)
        @Volatile var firstStartEntered: CountDownLatch? = null
        @Volatile var releaseFirstStart: CountDownLatch? = null

        override val incoming: Flow<TransportSignalEnvelopeV1> = flow {
            collectionStarts.incrementAndGet()
            source.collect { emit(it) }
        }
        override val state: StateFlow<DirectTransportSignalingState> = mutableState
        override val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities> =
            mutableCapabilities
        override val multipairV2Active = true

        override suspend fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig> {
            fetchCalls.incrementAndGet()
            return configs.removeFirst()
        }

        override fun start(): Boolean {
            val call = startCalls.incrementAndGet()
            if (call == 1) {
                firstStartEntered?.countDown()
                releaseFirstStart?.await(2, TimeUnit.SECONDS)
            }
            val accepted = if (startResults.isEmpty()) true else startResults.removeFirst()
            if (!accepted) return false
            mutableState.value = DirectTransportSignalingState.Connected(7)
            return true
        }

        override suspend fun issueTicket(
            targetDeviceId: UUID,
            connectionId: UUID,
            config: DirectTransportServerConfig,
        ): DirectSignalingCallResult<ValidatedDirectPathTicket> {
            issueTicketCalls.incrementAndGet()
            return DirectSignalingCallResult.Disabled
        }

        override fun send(envelope: TransportSignalEnvelopeV1): Boolean {
            sendCalls.incrementAndGet()
            sent += envelope
            return !closed.get()
        }

        fun publishState(value: DirectTransportSignalingState) {
            mutableState.value = value
        }

        suspend fun emit(value: TransportSignalEnvelopeV1) {
            source.emit(value)
        }

        override fun close() {
            if (closed.compareAndSet(false, true)) closeCalls.incrementAndGet()
            mutableState.value = DirectTransportSignalingState.Closed
        }
    }

    private fun signal(type: Int, sequence: Int, payloadBytes: Int = 0) =
        TransportSignalEnvelopeV1.newBuilder()
            .setSignalType(TransportSignalTypeV1.forNumber(type))
            .setSignalSeq(sequence.toLong())
            .setSenderDeviceId(ByteString.copyFrom(uuidBytes(SIGNAL_SENDER)))
            .setConnectionId(ByteString.copyFrom(uuidBytes(SIGNAL_CONNECTION)))
            .setPathEpoch(7)
            .setCiphertext(ByteString.copyFrom(ByteArray(payloadBytes) { sequence.toByte() }))
            .build()

    private fun uuidBytes(value: UUID): ByteArray = ByteBuffer.allocate(16)
        .putLong(value.mostSignificantBits)
        .putLong(value.leastSignificantBits)
        .array()

    private fun config(permitted: Boolean) = DirectTransportServerConfig(
        protocolVersion = 1,
        capabilities = buildSet {
            add(DirectTransportSignalingClient.DIRECT_SIGNALING_CAPABILITY)
            add(DirectTransportSignalingClient.DIRECT_SHADOW_CAPABILITY)
            add(DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY)
            if (permitted) {
                add(DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY)
                add(DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY)
            }
        },
        directRoutingPermitted = permitted,
        ticketSigningKeys = emptyMap(),
        stunServers = emptyList(),
    )

    private companion object {
        val SIGNAL_SENDER: UUID = UUID.fromString("10000000-0000-0000-0000-000000000001")
        val SIGNAL_CONNECTION: UUID = UUID.fromString("20000000-0000-0000-0000-000000000002")
    }
}

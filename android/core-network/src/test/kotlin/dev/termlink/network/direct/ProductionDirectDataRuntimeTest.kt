package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5
import dev.termlink.network.direct.nativebridge.DirectNativeCursorV5
import dev.termlink.network.direct.nativebridge.DirectNativeDirectionV5
import dev.termlink.network.direct.nativebridge.DirectNativeEventKindV5
import dev.termlink.network.direct.nativebridge.DirectNativeEventV5
import dev.termlink.network.direct.nativebridge.DirectNativeIngressSourceV5
import dev.termlink.network.direct.nativebridge.DirectNativeIngressV5
import dev.termlink.network.direct.nativebridge.DirectNativePathInfoV5
import dev.termlink.network.direct.nativebridge.DirectNativePathKindV5
import dev.termlink.network.direct.nativebridge.DirectNativePathV5
import dev.termlink.network.direct.nativebridge.DirectNativeProposalV5
import dev.termlink.network.direct.nativebridge.DirectNativeResultV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteEnvelopeV5
import dev.termlink.network.direct.nativebridge.DirectNativeSigningPayloadV5
import dev.termlink.network.direct.nativebridge.DirectNativeStatusV5
import dev.termlink.network.direct.nativebridge.DirectNativeTransitionV5
import dev.termlink.network.production.ProductionRealtimeMultiplexer
import dev.termlink.network.production.ProductionRealtimeNegotiatedCapabilities
import dev.termlink.network.production.ProductionRealtimeSessionTransport
import dev.termlink.network.production.ProductionRealtimeSocket
import dev.termlink.network.production.ProductionRealtimeSocketDiagnostics
import dev.termlink.network.production.ProductionReceivePressure
import dev.termlink.network.production.ProductionReceivedEnvelope
import dev.termlink.network.production.RealtimeState
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull

class ProductionDirectDataRuntimeTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val closeables = ArrayList<AutoCloseable>()

    @AfterTest
    fun tearDown() {
        closeables.asReversed().forEach { runCatching(it::close) }
        scope.cancel()
    }

    @Test
    fun incrementalCutoverRequiresDataAndSessionCapabilitiesOnBothPlanes() {
        fun admission(
            realtime: Set<String>,
            signaling: Set<String>,
        ) = ProductionDirectDataAdmission(
            serverRoutingPermitted = true,
            relayGeneration = 1,
            realtimeCapabilities = realtime,
            signalingGeneration = 2,
            signalingCapabilities = signaling,
            keyEpoch = 3,
        )
        val complete = setOf(DATA_CAPABILITY, SESSION_CUTOVER_CAPABILITY)

        assertTrue(admission(complete, complete).permitsNewCutover())
        assertFalse(admission(setOf(DATA_CAPABILITY), complete).permitsNewCutover())
        assertFalse(admission(complete, setOf(DATA_CAPABILITY)).permitsNewCutover())
    }

    @Test
    fun committedPeerSurvivesRelayLossAndStaleGateCannotReplaceIt(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 25)
        val peer = uuid(2)
        val first = FakeNative(path(1))
        val gate = fixture.runtime.captureGate(serverRoutingPermitted = true, keyEpoch = 7)
        assertTrue(fixture.runtime.installPromotedPeer(install(peer, first, gate, keyEpoch = 7)))
        assertTrue(fixture.runtime.ownsPromotedPath(peer, uuid(10), 1))
        assertFalse(fixture.runtime.ownsPromotedPath(peer, uuid(12), 1))
        assertFalse(fixture.runtime.ownsPromotedPath(peer, uuid(10), 2))
        assertFalse(fixture.runtime.ownsPromotedPath(uuid(99), uuid(10), 1))

        // Relay generation/capability is only a pre-cutover admission fence.
        fixture.realtime.value = ProductionRealtimeNegotiatedCapabilities(0, emptySet())
        assertTrue(fixture.runtime.updateAppliedCursor(peer, uuid(40), 12))
        withTimeout(2_000) {
            while (first.appliedCursors.get() != 1) delay(1)
        }
        assertFalse(first.closed.get())

        val successor = FakeNative(path(2))
        assertFalse(fixture.runtime.installPromotedPeer(install(peer, successor, gate, keyEpoch = 7)))
        assertTrue(successor.closed.get())
        assertTrue(fixture.runtime.updateAppliedCursor(peer, uuid(40), 13))
    }

    @Test
    fun signalOverflowRetiresOnlyTheExactPromotedPathFence(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(2)
        val native = FakeNative(path(1))
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )

        assertFalse(
            fixture.runtime.retirePromotedPath(
                peer, uuid(12), 1, ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
            ),
        )
        assertFalse(native.closed.get())
        assertTrue(
            fixture.runtime.retirePromotedPath(
                peer, uuid(10), 1, ProductionDirectPeerRetirementReason.DIRECT_FAILURE,
            ),
        )
        withTimeout(2_000) {
            while (!native.closed.get()) delay(1)
        }
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun closeAndJoinWaitsForEveryPeerActorAndNativeCleanup(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val first = FakeNative(path(1))
        val second = FakeNative(path(1, connectionSeed = 20, attemptSeed = 21))
        val gate = fixture.runtime.captureGate(true, 7)
        assertTrue(fixture.runtime.installPromotedPeer(install(uuid(2), first, gate, 7)))
        assertTrue(fixture.runtime.installPromotedPeer(install(uuid(3), second, gate, 7)))

        withTimeout(2_000) { fixture.runtime.closeAndJoin() }

        assertTrue(first.closed.get())
        assertTrue(second.closed.get())
        assertFalse(fixture.runtime.updateAppliedCursor(uuid(2), uuid(40), 1))
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun sessionDeregisterRunsBothDirectionsOnThePeerLaneAndIsRepeatable(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(2)
        val session = uuid(40)
        val native = FakeNative(path(1))
        assertTrue(fixture.runtime.installPromotedPeer(
            install(peer, native, fixture.runtime.captureGate(true, 7), 7),
        ))

        assertTrue(fixture.runtime.deregisterSession(peer, session))
        withTimeout(2_000) { while (native.deregisteredSessions.size != 2) delay(1) }
        assertEquals(
            setOf(DirectNativeDirectionV5.PC_TO_ANDROID, DirectNativeDirectionV5.ANDROID_TO_PC),
            native.deregisteredSessions.map { it.direction }.toSet(),
        )
        assertTrue(native.deregisteredSessions.all { it.highestContiguous == 0L })

        assertTrue(fixture.runtime.deregisterSession(peer, session))
        withTimeout(2_000) { while (native.deregisteredSessions.size != 4) delay(1) }
        assertFalse(native.closed.get())
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun sessionDeregisterQueueOverflowRetiresOnlyDirectPeerAndKeepsRelayConnected(): Unit = runBlocking {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val processor = ProductionDirectSignalProcessor { _, _ ->
            entered.countDown()
            release.await(2, TimeUnit.SECONDS)
            true
        }
        val fixture = fixture(
            queueFrames = 1,
            pumpIntervalMillis = 30_000,
            signalProcessor = processor,
        )
        val peer = uuid(2)
        val native = FakeNative(path(1))
        assertTrue(fixture.runtime.installPromotedPeer(
            install(peer, native, fixture.runtime.captureGate(true, 7), 7),
        ))
        assertTrue(fixture.runtime.offerSignal(peer, signal(peer, sequence = 1)))
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        assertTrue(fixture.runtime.offerSignal(peer, signal(peer, sequence = 2)))

        assertFalse(fixture.runtime.deregisterSession(peer, uuid(40)))
        release.countDown()
        withTimeout(2_000) { while (!native.closed.get()) delay(1) }

        assertFalse(fixture.runtime.hasPeer(peer))
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun staleInstallClosesRejectedNativeAndCannotBlockCloseAndJoin(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(2)
        val current = FakeNative(path(epoch = 2))
        val stale = FakeNative(path(epoch = 1))
        val sameEpoch = FakeNative(path(epoch = 2, attemptSeed = 22))
        val gate = fixture.runtime.captureGate(true, 7)

        assertTrue(fixture.runtime.installPromotedPeer(install(peer, current, gate, 7)))
        assertFalse(fixture.runtime.installPromotedPeer(install(peer, stale, gate, 7)))
        assertFalse(fixture.runtime.installPromotedPeer(install(peer, sameEpoch, gate, 7)))
        withTimeout(2_000) {
            while (!stale.closed.get() || !sameEpoch.closed.get()) delay(1)
        }

        assertFalse(current.closed.get())
        assertTrue(fixture.runtime.ownsPromotedPath(peer, uuid(10), 2))
        withTimeout(2_000) { fixture.runtime.closeAndJoin() }
        assertTrue(current.closed.get())
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun signalingGenerationChangeIsSerializedPerPeerWithoutRetiringCommittedRuntime(): Unit = runBlocking {
        val reconciled = AtomicInteger(0)
        val processor = object : ProductionDirectSignalProcessor {
            override fun process(
                actor: ProductionDirectDataPeerBoundary,
                envelope: TransportSignalEnvelopeV1,
            ) = true

            override fun signalingCapabilitiesChanged(
                actor: ProductionDirectDataPeerBoundary,
                value: DirectTransportSignalingNegotiatedCapabilities,
            ): Boolean {
                reconciled.incrementAndGet()
                return true
            }
        }
        val fixture = fixture(pumpIntervalMillis = 30_000, signalProcessor = processor)
        val negotiatingPeer = uuid(2)
        val committedPeer = uuid(3)
        val negotiating = FakeNative(path(1))
        val committed = FakeNative(path(1, connectionSeed = 20, attemptSeed = 21))
        committed.actions += carrierAction(
            committed.path, DirectNativeDirectionV5.PC_TO_ANDROID, DirectNativeCarrierV5.LAN_DIRECT,
        )
        committed.actions += carrierAction(
            committed.path, DirectNativeDirectionV5.ANDROID_TO_PC, DirectNativeCarrierV5.LAN_DIRECT,
        )
        val gate = fixture.runtime.captureGate(true, 7)
        assertTrue(fixture.runtime.installPromotedPeer(install(negotiatingPeer, negotiating, gate, 7)))
        assertTrue(fixture.runtime.installPromotedPeer(install(committedPeer, committed, gate, 7)))
        assertTrue(fixture.runtime.offerSignal(committedPeer, signal(committedPeer)))
        withTimeout(2_000) {
            while (fixture.runtime.carriers.value[committedPeer] !=
                ProductionDirectPeerCarrier.LAN_DIRECT
            ) delay(1)
        }

        fixture.runtime.signalingCapabilitiesChanged(
            DirectTransportSignalingNegotiatedCapabilities(7, emptySet()),
        )
        withTimeout(2_000) { while (reconciled.get() != 2) delay(1) }

        assertFalse(negotiating.closed.get())
        assertFalse(committed.closed.get())
        assertEquals(ProductionDirectPeerCarrier.LAN_DIRECT, fixture.runtime.carriers.value[committedPeer])
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun envelopeAvailableAndSignRequestAreDrainedOnceAndSecretsAreCleared(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(3)
        val native = FakeNative(path(1))
        val exact = outputEnvelope(uuid(44), peer, uuid(1), sequence = 9).toByteArray()
        native.incoming += DirectNativeDataEnvelope(4, exact.copyOf())
        native.signing += DirectNativeSigningPayloadV5(77, byteArrayOf(7, 8, 9))
        native.events += event(native.path, DirectNativeEventKindV5.ENVELOPE_AVAILABLE)
        native.events += event(native.path, DirectNativeEventKindV5.SIGN_REQUEST, frameSequence = 1)
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )

        assertTrue(fixture.runtime.offerSignal(peer, signal(peer)))
        withTimeout(2_000) {
            while (native.submittedSignatures.get() != 1 || native.ingress.isEmpty()) delay(1)
        }
        val admitted = native.ingress.single()
        assertEquals(DirectNativeIngressSourceV5.DIRECT, admitted.first.source)
        assertTrue(admitted.second.contentEquals(exact))
        assertEquals(1, native.routePolls.get(), "empty action queue must stop after its first NO_DATA")
        assertEquals(3, native.eventPolls.get(), "two events plus one empty poll")
        assertTrue(fixture.signedTranscripts.single().contentEquals(byteArrayOf(7, 8, 9)))
        assertEquals(64, native.lastSignature?.size)
        exact.fill(0)
    }

    @Test
    fun queueOverflowAndNativeFailureRetireOnlyTheirPeerAndNeverRelay(): Unit = runBlocking {
        val blockedPeer = uuid(4)
        val healthyPeer = uuid(5)
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val fixture = fixture(
            queueFrames = 1,
            pumpIntervalMillis = 30_000,
            signalProcessor = ProductionDirectSignalProcessor { actor, _ ->
                if (actor.peerDeviceId == blockedPeer) {
                    entered.countDown()
                    release.await(2, TimeUnit.SECONDS)
                }
                true
            },
        )
        val blockedNative = FakeNative(path(1))
        val healthyNative = FakeNative(path(1, connectionSeed = 13, attemptSeed = 14))
        val gate = fixture.runtime.captureGate(true, 7)
        assertTrue(fixture.runtime.installPromotedPeer(install(blockedPeer, blockedNative, gate, 7)))
        assertTrue(fixture.runtime.installPromotedPeer(install(healthyPeer, healthyNative, gate, 7)))

        assertTrue(fixture.runtime.offerSignal(blockedPeer, signal(blockedPeer, 1)))
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        assertTrue(fixture.runtime.offerSignal(blockedPeer, signal(blockedPeer, 2)))
        assertFalse(fixture.runtime.offerSignal(blockedPeer, signal(blockedPeer, 3)))
        assertTrue(fixture.runtime.offerSignal(healthyPeer, signal(healthyPeer, 1)))
        release.countDown()

        withTimeout(2_000) {
            while (!blockedNative.closed.get()) delay(1)
        }
        assertFalse(fixture.runtime.updateAppliedCursor(blockedPeer, uuid(40), 1))
        assertTrue(fixture.runtime.updateAppliedCursor(healthyPeer, uuid(40), 1))
        withTimeout(2_000) {
            while (healthyNative.appliedCursors.get() != 1) delay(1)
        }
        assertEquals(0, fixture.socket.disconnects.get())

        healthyNative.failRoutePoll.set(true)
        assertTrue(fixture.runtime.offerSignal(healthyPeer, signal(healthyPeer, 2)))
        withTimeout(2_000) {
            while (!healthyNative.closed.get()) delay(1)
        }
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun outboundNativeFailureReplaysTheFirstExactBytesOnRelay(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(6)
        val native = FakeNative(path(1)).also { it.failRouteEnvelope.set(true) }
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        val envelope = inputEnvelope(uuid(45), uuid(1), peer, 11)
        val exact = envelope.toByteArray()
        var synchronousFallbacks = 0
        assertTrue(
            fixture.runtime.routeEligibleOutbound(peer, envelope, exact) {
                synchronousFallbacks++
                true
            },
        )
        withTimeout(2_000) {
            while (fixture.relayFallbacks.isEmpty()) delay(1)
        }
        assertEquals(0, synchronousFallbacks)
        assertTrue(fixture.relayFallbacks.single().contentEquals(exact))
        assertEquals(0, fixture.socket.disconnects.get())
        exact.fill(0)
    }

    @Test
    fun networkChangeRunsAfterAlreadyAcceptedWorkOnTheSameActorLane(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(7)
        val native = FakeNative(path(1))
        val routeEntered = CountDownLatch(1)
        val releaseRoute = CountDownLatch(1)
        native.routeEntered = routeEntered
        native.releaseRoute = releaseRoute
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )

        val envelope = inputEnvelope(uuid(46), uuid(1), peer, 1)
        val exact = envelope.toByteArray()
        assertTrue(fixture.runtime.routeEligibleOutbound(peer, envelope, exact) { false })
        assertTrue(routeEntered.await(2, TimeUnit.SECONDS))

        try {
            fixture.runtime.networkChanged()
            assertFalse(
                native.networkChangedEntered.await(150, TimeUnit.MILLISECONDS),
                "networkChanged must be queued behind accepted work, not race JNI from the caller thread",
            )
        } finally {
            releaseRoute.countDown()
        }

        assertTrue(native.networkChangedEntered.await(2, TimeUnit.SECONDS))
        assertTrue(native.routeThread.get() === native.networkChangedThread.get())
        assertEquals(1, native.networkChanges.get())
        assertEquals(0, fixture.socket.disconnects.get())
        exact.fill(0)
    }

    @Test
    fun periodicPumpIsCoalescedAndCannotConsumeTheWholePeerQueue(): Unit = runBlocking {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val fixture = fixture(
            queueFrames = 2,
            pumpIntervalMillis = 1,
            signalProcessor = ProductionDirectSignalProcessor { _, _ ->
                entered.countDown()
                release.await(2, TimeUnit.SECONDS)
                true
            },
        )
        val peer = uuid(8)
        val native = FakeNative(path(1))
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        assertTrue(fixture.runtime.offerSignal(peer, signal(peer)))
        assertTrue(entered.await(2, TimeUnit.SECONDS))

        try {
            delay(100)
            assertTrue(
                fixture.runtime.updateAppliedCursor(peer, uuid(47), 1),
                "at most one pending Pump may occupy the bounded data/control queue",
            )
        } finally {
            release.countDown()
        }

        withTimeout(2_000) {
            while (native.appliedCursors.get() != 1) delay(1)
        }
        assertFalse(native.closed.get())
    }

    @Test
    fun nativeFailureDrainsTheFullBoundedRelayFallbackTailBeforeClose(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(9)
        val native = FakeNative(path(1))
        val expected = (1..MAX_NATIVE_ENVELOPES).map { sequence ->
            inputEnvelope(uuid(48), uuid(1), peer, sequence.toLong()).toByteArray()
        }
        expected.forEachIndexed { index, bytes ->
            native.actions += relayEnvelopeAction(native.path, uuid(48), index + 1L, bytes)
        }
        native.events += event(native.path, DirectNativeEventKindV5.DIRECT_FAILED)
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )

        assertTrue(fixture.runtime.offerSignal(peer, signal(peer)))
        withTimeout(5_000) {
            while (!native.closed.get()) delay(1)
        }

        assertEquals(MAX_NATIVE_ENVELOPES, fixture.relayFallbacks.size)
        expected.zip(fixture.relayFallbacks).forEach { (wanted, actual) ->
            assertTrue(wanted.contentEquals(actual))
            wanted.fill(0)
        }
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun transientRelayFailureCannotDiscardTheNativeFallbackTail(): Unit = runBlocking {
        val attempts = AtomicInteger(0)
        val delivered = java.util.concurrent.CopyOnWriteArrayList<ByteArray>()
        val fixture = fixture(
            pumpIntervalMillis = 30_000,
            relaySender = ProductionDirectRelayExactSender { _, bytes ->
                if (attempts.incrementAndGet() == 1) {
                    false
                } else {
                    delivered += bytes.copyOf()
                    true
                }
            },
        )
        val peer = uuid(10)
        val native = FakeNative(path(1))
        val expected = (1..3).map { sequence ->
            inputEnvelope(uuid(49), uuid(1), peer, sequence.toLong()).toByteArray()
        }
        expected.forEachIndexed { index, bytes ->
            native.actions += relayEnvelopeAction(native.path, uuid(49), index + 1L, bytes)
        }
        native.events += event(native.path, DirectNativeEventKindV5.DIRECT_FAILED)
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )

        assertTrue(fixture.runtime.offerSignal(peer, signal(peer)))
        withTimeout(5_000) {
            while (!native.closed.get() || delivered.size != expected.size) delay(1)
        }

        assertTrue(attempts.get() > expected.size)
        assertEquals(expected.size, delivered.size)
        expected.zip(delivered).forEach { (wanted, actual) ->
            assertTrue(wanted.contentEquals(actual))
            wanted.fill(0)
        }
        assertEquals(0, fixture.socket.disconnects.get())
    }

    @Test
    fun heldRelayIngressIsRecoveredWhenNativeQueueIsFull(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(11)
        val session = uuid(50)
        val endpoint = fixture.subscribedEndpoint(session, peer)
        val native = FakeNative(path(1)).also { it.failArbitrateIngress.set(true) }
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        val exact = outputEnvelope(session, peer, uuid(1), 1).toByteArray()
        val expected = exact.copyOf()
        val delivered = async { endpoint.receivedEnvelopes.first().use { it.copyRawBytes() } }

        assertTrue(fixture.runtime.offerRelay(peer, relayReceived(exact, generation = 1)))
        val actual = withTimeoutOrNull(2_000) { delivered.await() }
        assertNotNull(actual, "no Relay recovery delivery; ${endpoint.diagnostics()}")
        assertTrue(expected.contentEquals(actual))
        assertEquals(0, fixture.socket.disconnects.get())
        expected.fill(0)
    }

    @Test
    fun heldRelayIngressIsRecoveredOnSerializedNetworkChange(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(12)
        val session = uuid(51)
        val endpoint = fixture.subscribedEndpoint(session, peer)
        val native = FakeNative(path(1))
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        val exact = outputEnvelope(session, peer, uuid(1), 1).toByteArray()
        val expected = exact.copyOf()
        val delivered = async { endpoint.receivedEnvelopes.first().use { it.copyRawBytes() } }
        assertTrue(fixture.runtime.offerRelay(peer, relayReceived(exact, generation = 1)))
        withTimeout(2_000) {
            while (native.ingress.size != 1) delay(1)
        }
        native.actionsOnNetworkChanged += ingressDeliveryAction(
            native.path,
            session,
            1,
            DirectNativeCarrierV5.RELAY,
            expected,
        )

        fixture.runtime.networkChanged()
        assertTrue(expected.contentEquals(withTimeout(2_000) { delivered.await() }))
        assertEquals(1, native.networkChanges.get())
        assertEquals(0, fixture.socket.disconnects.get())
        expected.fill(0)
    }

    @Test
    fun heldRelayIngressIsRecoveredWhenRuntimeCloses(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(13)
        val session = uuid(52)
        val endpoint = fixture.subscribedEndpoint(session, peer)
        val native = FakeNative(path(1))
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        val exact = outputEnvelope(session, peer, uuid(1), 1).toByteArray()
        val expected = exact.copyOf()
        val delivered = async { endpoint.receivedEnvelopes.first().use { it.copyRawBytes() } }
        assertTrue(fixture.runtime.offerRelay(peer, relayReceived(exact, generation = 1)))
        withTimeout(2_000) {
            while (native.ingress.size != 1) delay(1)
        }
        native.actionsOnCancel += ingressDeliveryAction(
            native.path,
            session,
            1,
            DirectNativeCarrierV5.RELAY,
            expected,
        )

        fixture.runtime.close()
        assertTrue(expected.contentEquals(withTimeout(2_000) { delivered.await() }))
        assertEquals(1, native.cancels.get())
        assertEquals(0, fixture.socket.disconnects.get())
        expected.fill(0)
    }

    @Test
    fun relayDirectConflictDeliversExactlyOneOwnedEnvelope(): Unit = runBlocking {
        val fixture = fixture(pumpIntervalMillis = 30_000)
        val peer = uuid(14)
        val session = uuid(53)
        val endpoint = fixture.subscribedEndpoint(session, peer)
        val native = FakeNative(path(1))
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, native, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        val relayExact = outputEnvelope(session, peer, uuid(1), 1).toByteArray()
        val expected = relayExact.copyOf()
        val delivered = async { endpoint.receivedEnvelopes.first().use { it.copyRawBytes() } }
        assertTrue(fixture.runtime.offerRelay(peer, relayReceived(relayExact, generation = 1)))
        withTimeout(2_000) {
            while (native.ingress.size != 1) delay(1)
        }
        native.actions += ingressDeliveryAction(
            native.path,
            session,
            1,
            DirectNativeCarrierV5.RELAY,
            expected,
        )
        native.actions += ingressMarkerAction(
            native.path,
            session,
            1,
            DirectNativeCarrierV5.LAN_DIRECT,
            DirectNativeRouteActionKindV5.INGRESS_DROP,
            expected.size,
        )

        assertTrue(fixture.runtime.offerDirect(peer, expected))
        assertTrue(expected.contentEquals(withTimeout(2_000) { delivered.await() }))
        assertEquals(
            null,
            withTimeoutOrNull(200) { endpoint.receivedEnvelopes.first() },
            "the Relay/Direct duplicate must not be delivered twice",
        )
        assertEquals(0, fixture.socket.disconnects.get())
        expected.fill(0)
    }

    @Test
    fun replacedActorCannotDeliverLateDirectBytesAcrossTheCurrentFence(): Unit = runBlocking {
        val peer = uuid(15)
        val session = uuid(54)
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val fixture = fixture(
            pumpIntervalMillis = 30_000,
            signalProcessor = ProductionDirectSignalProcessor { actor, _ ->
                if (actor.pathFence.pathEpoch == 1L) {
                    entered.countDown()
                    release.await(2, TimeUnit.SECONDS)
                }
                true
            },
        )
        val endpoint = fixture.subscribedEndpoint(session, peer)
        val first = FakeNative(path(1))
        val successor = FakeNative(path(2, connectionSeed = 16, attemptSeed = 17))
        val exact = outputEnvelope(session, peer, uuid(1), 1).toByteArray()
        first.actions += ingressDeliveryAction(
            first.path,
            session,
            1,
            DirectNativeCarrierV5.LAN_DIRECT,
            exact,
        )
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, first, fixture.runtime.captureGate(true, 7), 7),
            ),
        )
        assertTrue(fixture.runtime.offerSignal(peer, signal(peer)))
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        assertTrue(
            fixture.runtime.installPromotedPeer(
                install(peer, successor, fixture.runtime.captureGate(true, 7), 7),
            ),
        )

        release.countDown()
        assertEquals(
            null,
            withTimeoutOrNull(300) { endpoint.receivedEnvelopes.first() },
            "an actor replaced in the registry must lose authority before any queued delivery",
        )
        assertTrue(fixture.runtime.updateAppliedCursor(peer, session, 1))
        withTimeout(2_000) {
            while (successor.appliedCursors.get() != 1) delay(1)
        }
        assertEquals(0, fixture.socket.disconnects.get())
        exact.fill(0)
    }

    private fun fixture(
        queueFrames: Int = 8,
        pumpIntervalMillis: Long,
        signalProcessor: ProductionDirectSignalProcessor = ProductionDirectSignalProcessor { _, _ -> true },
        relaySender: ProductionDirectRelayExactSender? = null,
    ): Fixture {
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, scope)
        closeables += multiplexer
        val realtime = MutableStateFlow(
            ProductionRealtimeNegotiatedCapabilities(
                5,
                setOf(DATA_CAPABILITY, SESSION_CUTOVER_CAPABILITY),
            ),
        )
        val signal = FakeSignalLane(6, setOf(DATA_CAPABILITY, SESSION_CUTOVER_CAPABILITY))
        val relayed = java.util.concurrent.CopyOnWriteArrayList<ByteArray>()
        val signed = java.util.concurrent.CopyOnWriteArrayList<ByteArray>()
        val runtime = ProductionDirectDataRuntime(
            multiplexer = multiplexer,
            realtimeCapabilities = realtime,
            signaling = signal,
            relaySender = relaySender ?: ProductionDirectRelayExactSender { _, bytes ->
                relayed += bytes.copyOf()
                true
            },
            signalProcessor = signalProcessor,
            signer = ProductionDirectDataSigner { transcript ->
                signed += transcript.copyOf()
                ByteArray(64) { 9 }
            },
            parentScope = scope,
            enabled = true,
            queueFramesPerPeer = queueFrames,
            queueBytesPerPeer = 1024 * 1024,
            pumpIntervalMillis = pumpIntervalMillis,
        )
        closeables += runtime
        return Fixture(runtime, multiplexer, realtime, signal, socket, relayed, signed)
    }

    private data class Fixture(
        val runtime: ProductionDirectDataRuntime,
        val multiplexer: ProductionRealtimeMultiplexer,
        val realtime: MutableStateFlow<ProductionRealtimeNegotiatedCapabilities>,
        val signal: FakeSignalLane,
        val socket: FakeSocket,
        val relayFallbacks: List<ByteArray>,
        val signedTranscripts: List<ByteArray>,
    )

    private class FakeSignalLane(generation: Int, capabilities: Set<String>) : ProductionDirectDataSignalLane {
        override val negotiatedCapabilities = MutableStateFlow(
            DirectTransportSignalingNegotiatedCapabilities(generation, capabilities),
        )
        override fun send(envelope: TransportSignalEnvelopeV1) = true
    }

    private class FakeSocket : ProductionRealtimeSocket {
        override val receivedEnvelopes = kotlinx.coroutines.flow.emptyFlow<ProductionReceivedEnvelope>()
        override val receivePressure: StateFlow<ProductionReceivePressure> = MutableStateFlow(
            ProductionReceivePressure(0, false, 0, 0),
        )
        override val state = MutableSharedFlow<RealtimeState>(replay = 1, extraBufferCapacity = 4)
        val disconnects = AtomicInteger(0)
        override fun connect() = Unit
        override fun send(envelope: Envelope) = true
        override fun disconnect(reason: String) { disconnects.incrementAndGet() }
        override fun diagnostics() = ProductionRealtimeSocketDiagnostics(1, 1, true, "CONNECTED", 0, 0)
        override fun close() = Unit
    }

    private class FakeNative(override val path: DirectNativePathV5) : ProductionDirectDataNative {
        val events = ConcurrentLinkedQueue<DirectNativeEventV5>()
        val actions = ConcurrentLinkedQueue<DirectNativeRouteActionV5>()
        val incoming = ConcurrentLinkedQueue<DirectNativeDataEnvelope>()
        val signing = ConcurrentLinkedQueue<DirectNativeSigningPayloadV5>()
        val ingress = java.util.concurrent.CopyOnWriteArrayList<Pair<DirectNativeIngressV5, ByteArray>>()
        val deregisteredSessions = java.util.concurrent.CopyOnWriteArrayList<DirectNativeCursorV5>()
        val routePolls = AtomicInteger(0)
        val eventPolls = AtomicInteger(0)
        val appliedCursors = AtomicInteger(0)
        val submittedSignatures = AtomicInteger(0)
        val networkChanges = AtomicInteger(0)
        val cancels = AtomicInteger(0)
        val routeThread = AtomicReference<Thread?>()
        val networkChangedThread = AtomicReference<Thread?>()
        val networkChangedEntered = CountDownLatch(1)
        val actionsOnNetworkChanged = ConcurrentLinkedQueue<DirectNativeRouteActionV5>()
        val actionsOnCancel = ConcurrentLinkedQueue<DirectNativeRouteActionV5>()
        val closed = AtomicBoolean(false)
        val failRoutePoll = AtomicBoolean(false)
        val failRouteEnvelope = AtomicBoolean(false)
        val failArbitrateIngress = AtomicBoolean(false)
        @Volatile var routeEntered: CountDownLatch? = null
        @Volatile var releaseRoute: CountDownLatch? = null
        @Volatile var lastSignature: ByteArray? = null

        override fun pathInfo() = DirectNativeResultV5.Success(
            DirectNativePathInfoV5(path, DirectNativePathKindV5.LAN, true, ByteArray(32) { 1 }),
        )
        override fun startProposal(value: DirectNativeProposalV5) = ok()
        override fun receiveProposal(value: DirectNativeProposalV5) = ok()
        override fun receiveAccept(value: DirectNativeTransitionV5) = ok()
        override fun receiveCommit(value: DirectNativeTransitionV5) = ok()
        override fun commitEchoSent(value: DirectNativeTransitionV5) = ok()
        override fun receiveFallback(value: DirectNativeTransitionV5) = ok()
        override fun tick(value: DirectNativeTransitionV5) = ok()
        override fun updateAppliedCursor(value: DirectNativeCursorV5): DirectNativeResultV5<Unit> {
            appliedCursors.incrementAndGet()
            return ok()
        }
        override fun deregisterSession(value: DirectNativeCursorV5): DirectNativeResultV5<Unit> {
            deregisteredSessions += value
            return ok()
        }
        override fun routeEnvelope(
            value: DirectNativeRouteEnvelopeV5,
            exactEnvelope: ByteArray,
        ): DirectNativeResultV5<Unit> {
            routeThread.set(Thread.currentThread())
            routeEntered?.countDown()
            releaseRoute?.await(2, TimeUnit.SECONDS)
            return if (failRouteEnvelope.get()) failure() else ok()
        }
        override fun arbitrateIngress(value: DirectNativeIngressV5, exactEnvelope: ByteArray): DirectNativeResultV5<Unit> {
            ingress += value to exactEnvelope.copyOf()
            return if (failArbitrateIngress.get()) {
                failure(nativeStatus = DirectNativeStatusV5.QUEUE_FULL)
            } else {
                ok()
            }
        }
        override fun tickIngress(nowMillis: Long) = ok()
        override fun pollRouteAction(): DirectNativeResultV5<DirectNativeRouteActionV5?> {
            routePolls.incrementAndGet()
            return if (failRoutePoll.get()) failure() else DirectNativeResultV5.Success(actions.poll())
        }
        override fun pollEvent(): DirectNativeResultV5<DirectNativeEventV5?> {
            eventPolls.incrementAndGet()
            return DirectNativeResultV5.Success(events.poll())
        }
        override fun receiveSigningPayload() = DirectNativeResultV5.Success(signing.poll())
        override fun submitSignature(requestId: Long, signature: ByteArray): DirectNativeResultV5<Unit> {
            lastSignature = signature.copyOf()
            submittedSignatures.incrementAndGet()
            return ok()
        }
        override fun receiveEnvelope() = DirectNativeResultV5.Success(incoming.poll())
        override fun cancel(): DirectNativeResultV5<Unit> {
            cancels.incrementAndGet()
            drainActions(actionsOnCancel)
            return ok()
        }
        override fun networkChanged(): DirectNativeResultV5<Unit> {
            networkChangedThread.set(Thread.currentThread())
            networkChanges.incrementAndGet()
            drainActions(actionsOnNetworkChanged)
            networkChangedEntered.countDown()
            return ok()
        }
        override fun close() { closed.set(true) }
        private fun drainActions(source: ConcurrentLinkedQueue<DirectNativeRouteActionV5>) {
            while (true) actions += source.poll() ?: return
        }
        private fun ok(): DirectNativeResultV5<Unit> = DirectNativeResultV5.Success(Unit)
        private fun failure(nativeStatus: Int? = null): DirectNativeResultV5.Failure =
            DirectNativeResultV5.Failure(2_003, nativeStatus, path.epoch, path.attemptId.copyOf())
    }

    private fun install(
        peer: UUID,
        native: FakeNative,
        gate: ProductionDirectDataGate,
        keyEpoch: Long,
    ) = ProductionDirectDataPeerInstall(
        peer,
        ProductionDirectPathFence(
            native.path.epoch,
            native.path.connectionId.toUuid(),
            native.path.attemptId.toUuid(),
        ),
        keyEpoch,
        native,
        gate,
    )

    private suspend fun Fixture.subscribedEndpoint(
        sessionId: UUID,
        peerDeviceId: UUID,
    ): ProductionRealtimeSessionTransport {
        val endpoint = multiplexer.register(sessionId, peerDeviceId)
        closeables += endpoint
        endpoint.connect()
        socket.state.emit(RealtimeState.Connected)
        withTimeout(2_000) {
            while (endpoint.currentConnectedGeneration() != 1) delay(1)
        }
        assertTrue(
            endpoint.sendForConnectedGeneration(
                Envelope.newBuilder()
                    .setProtocolVersion(1)
                    .setMessageType(MessageType.SUBSCRIBE)
                    .setDirection(Direction.ANDROID_TO_PC)
                    .setSessionId(ByteString.copyFrom(uuidBytes(sessionId)))
                    .setSenderDeviceId(ByteString.copyFrom(uuidBytes(uuid(1))))
                    .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(peerDeviceId)))
                    .build(),
                expectedGeneration = 1,
            ),
        )
        return endpoint
    }

    private fun relayReceived(exactBytes: ByteArray, generation: Int): ProductionReceivedEnvelope {
        val owned = exactBytes.copyOf()
        return ProductionReceivedEnvelope(
            envelope = Envelope.parseFrom(owned),
            wireBytes = owned.size,
            generation = generation,
            releaseReservation = { },
            rawBytes = owned,
        )
    }

    private fun relayEnvelopeAction(
        path: DirectNativePathV5,
        sessionId: UUID,
        sequence: Long,
        payload: ByteArray,
    ) = DirectNativeRouteActionV5(
        path = path,
        kind = DirectNativeRouteActionKindV5.RELAY_ENVELOPE,
        direction = DirectNativeDirectionV5.ANDROID_TO_PC,
        sessionId = uuidBytes(sessionId),
        envelopeSequence = sequence,
        directFrameSequence = 0,
        barrierId = ByteArray(16),
        barrierSha256 = ByteArray(32),
        accepted = false,
        isEcho = false,
        carrier = DirectNativeCarrierV5.RELAY,
        flags = 0,
        reason = 1,
        payloadBytes = payload.size,
        payload = payload.copyOf(),
    )

    private fun ingressDeliveryAction(
        path: DirectNativePathV5,
        sessionId: UUID,
        sequence: Long,
        carrier: DirectNativeCarrierV5,
        payload: ByteArray,
    ) = DirectNativeRouteActionV5(
        path = path,
        kind = DirectNativeRouteActionKindV5.INGRESS_DELIVER,
        direction = DirectNativeDirectionV5.PC_TO_ANDROID,
        sessionId = uuidBytes(sessionId),
        envelopeSequence = sequence,
        directFrameSequence = 0,
        barrierId = ByteArray(16),
        barrierSha256 = ByteArray(32),
        accepted = false,
        isEcho = false,
        carrier = carrier,
        flags = 0,
        reason = 0,
        payloadBytes = payload.size,
        payload = payload.copyOf(),
    )

    private fun ingressMarkerAction(
        path: DirectNativePathV5,
        sessionId: UUID,
        sequence: Long,
        carrier: DirectNativeCarrierV5,
        kind: DirectNativeRouteActionKindV5,
        payloadBytes: Int,
    ) = DirectNativeRouteActionV5(
        path = path,
        kind = kind,
        direction = DirectNativeDirectionV5.PC_TO_ANDROID,
        sessionId = uuidBytes(sessionId),
        envelopeSequence = sequence,
        directFrameSequence = 0,
        barrierId = ByteArray(16),
        barrierSha256 = ByteArray(32),
        accepted = false,
        isEcho = false,
        carrier = carrier,
        flags = 0,
        reason = 0,
        payloadBytes = payloadBytes,
        payload = ByteArray(0),
    )

    private fun carrierAction(
        path: DirectNativePathV5,
        direction: DirectNativeDirectionV5,
        carrier: DirectNativeCarrierV5,
    ) = DirectNativeRouteActionV5(
        path = path,
        kind = DirectNativeRouteActionKindV5.CARRIER,
        direction = direction,
        sessionId = ByteArray(16),
        envelopeSequence = 0,
        directFrameSequence = 0,
        barrierId = ByteArray(16),
        barrierSha256 = ByteArray(32),
        accepted = false,
        isEcho = false,
        carrier = carrier,
        flags = 0,
        reason = 0,
        payloadBytes = 0,
        payload = ByteArray(0),
    )

    private fun path(epoch: Long, connectionSeed: Int = 10, attemptSeed: Int = 11) =
        DirectNativePathV5(epoch, uuidBytes(uuid(connectionSeed)), uuidBytes(uuid(attemptSeed)))

    private fun event(
        path: DirectNativePathV5,
        kind: DirectNativeEventKindV5,
        frameSequence: Long = 0,
    ) = DirectNativeEventV5(kind, 0, 0, path.epoch, path.attemptId.copyOf(), frameSequence, 0, 0)

    private fun signal(peer: UUID, sequence: Long = 1) = TransportSignalEnvelopeV1.newBuilder()
        .setProtocolVersion(1)
        .setSignalType(TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_CLOSE)
        .setConnectionId(ByteString.copyFrom(uuidBytes(uuid(10))))
        .setSenderDeviceId(ByteString.copyFrom(uuidBytes(peer)))
        .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(uuid(1))))
        .setPathEpoch(1)
        .setSignalSeq(sequence)
        .build()

    private fun outputEnvelope(session: UUID, peer: UUID, local: UUID, sequence: Long) =
        Envelope.newBuilder()
            .setProtocolVersion(1)
            .setMessageType(MessageType.OUTPUT_CHUNK)
            .setDirection(Direction.PC_TO_ANDROID)
            .setSessionId(ByteString.copyFrom(uuidBytes(session)))
            .setSenderDeviceId(ByteString.copyFrom(uuidBytes(peer)))
            .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(local)))
            .setSeq(sequence)
            .build()

    private fun inputEnvelope(session: UUID, local: UUID, peer: UUID, sequence: Long) =
        Envelope.newBuilder()
            .setProtocolVersion(1)
            .setMessageType(MessageType.INPUT_EVENT)
            .setDirection(Direction.ANDROID_TO_PC)
            .setSessionId(ByteString.copyFrom(uuidBytes(session)))
            .setSenderDeviceId(ByteString.copyFrom(uuidBytes(local)))
            .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(peer)))
            .setSeq(sequence)
            .build()

    private fun uuid(seed: Int) = UUID(0, seed.toLong())
    private fun uuidBytes(value: UUID) = java.nio.ByteBuffer.allocate(16)
        .putLong(value.mostSignificantBits)
        .putLong(value.leastSignificantBits)
        .array()
    private fun ByteArray.toUuid(): UUID = java.nio.ByteBuffer.wrap(this).let { UUID(it.long, it.long) }

    private companion object {
        const val DATA_CAPABILITY = "direct_transport_data_v1"
        const val SESSION_CUTOVER_CAPABILITY = "direct_session_cutover_v1"
        const val MAX_NATIVE_ENVELOPES = 256
    }
}

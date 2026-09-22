package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.DirectTransportCrypto
import dev.termlink.network.direct.nativebridge.DirectNativeEvent
import dev.termlink.network.direct.nativebridge.DirectNativeEventKind
import dev.termlink.network.direct.nativebridge.DirectNativeGatherRequest
import dev.termlink.network.direct.nativebridge.DirectNativeNominateRequest
import dev.termlink.network.direct.nativebridge.DirectNativePathState
import dev.termlink.network.direct.nativebridge.DirectNativeResult
import dev.termlink.network.direct.nativebridge.DirectNativeSocketAddress
import dev.termlink.network.direct.nativebridge.DirectNativeSyntheticReason
import dev.termlink.network.production.ProductionRealtimeSessionConnectionEvent
import dev.termlink.protocol.transport.v1.DirectCandidateKindV1
import dev.termlink.protocol.transport.v1.DirectCandidateV1
import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectPathAnswerV1
import dev.termlink.protocol.transport.v1.DirectPathResultV1
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalPayloadV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.lang.reflect.Modifier
import java.net.InetAddress
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProductionDirectShadowRuntimeTest {
    private val now = 1_900_000_000_000L

    @Test
    fun peerSelectionIsBoundedRoundRobinAndSkipsIneligiblePeers() {
        val peers = listOf("first", "second", "third")
        val first = requireNotNull(selectNextProductionDirectPeer(peers, 0) { it != "third" })
        val second = requireNotNull(
            selectNextProductionDirectPeer(peers, first.nextCursor) { it != "third" },
        )
        val wrapped = requireNotNull(
            selectNextProductionDirectPeer(peers, second.nextCursor) { it != "third" },
        )

        assertEquals("first", first.value)
        assertEquals("second", second.value)
        assertEquals("first", wrapped.value)
        assertEquals(null, selectNextProductionDirectPeer(peers, 2) { false })
    }

    @Test
    fun unchangedPeerRefreshDoesNotRetireActiveAttempt() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val native = FakeNative(holdBeforeShadowReady = true)
        val signaling = FakeSignaling(now, androidId, pcId, pairwise)
        val runtime = runtime(this, identity, signaling, native, FakePlatform())
        val initial = peer()
        val refreshed = peer()
        try {
            arm(runtime, initial, relayGeneration = 1)
            advanceTimeBy(100)
            runCurrent()
            assertEquals(ProductionDirectShadowState.NEGOTIATING, runtime.snapshot.value.state)

            assertTrue(runtime.replaceAuthorizedPeers(AUTH_GENERATION, listOf(refreshed)))
            runCurrent()

            assertEquals(ProductionDirectShadowState.NEGOTIATING, runtime.snapshot.value.state)
            assertEquals(0, native.cancelCalls)
            assertEquals(1, signaling.offers)
        } finally {
            initial.close()
            refreshed.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    private val androidId = UUID.fromString("10111213-1415-4617-9819-1a1b1c1d1e1f")
    private val pcId = UUID.fromString("20212223-2425-4627-a829-2a2b2c2d2e2f")
    private val pairwise = ByteArray(32) { (it + 1).toByte() }

    @Test
    fun initiatorFlowUsesExactNetworkCandidateSignatureFencesAndEndsShadowOnly() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val native = FakeNative(includeStaleEvents = true)
        val signaling = FakeSignaling(now, androidId, pcId, pairwise)
        val platform = FakePlatform()
        val runtime = runtime(this, identity, signaling, native, platform)
        val relay = FakeRelay()
        val callerPeer = peer()
        try {
            assertTrue(runtime.start())
            assertTrue(runtime.replaceAuthorizedPeers(AUTH_GENERATION, listOf(callerPeer)))
            assertTrue(runtime.observeCapturedNetwork(AUTH_GENERATION, network(1, 101L)))
            relay.connect(41)
            assertTrue(runtime.observeRelayConnected(AUTH_GENERATION, relay.generation))

            advanceTimeBy(250)
            runCurrent()

            val snapshot = runtime.snapshot.value
            assertEquals(snapshot.toString(), ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, snapshot.state)
            assertFalse(snapshot.directRoutingPermitted)
            assertFalse(runtime.directRoutingPermitted)
            assertEquals(41, snapshot.relayGeneration)
            assertEquals(pcId, snapshot.peerDeviceId)
            val diagnostic = runtime.diagnostic.value
            assertEquals(ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, diagnostic.state)
            assertEquals("NONE", diagnostic.reasonCode)
            assertEquals(AUTH_GENERATION, diagnostic.authenticationGeneration)
            assertEquals(41, diagnostic.relayGeneration)
            assertEquals(1L, diagnostic.networkGeneration)
            assertEquals(31L, diagnostic.pathEpoch)
            assertEquals(1, diagnostic.selectedPairRank)
            assertEquals(
                ProductionDirectShadowDiagnosticCandidateKind.HOST,
                diagnostic.localCandidateKind,
            )
            assertEquals(
                ProductionDirectShadowDiagnosticCandidateKind.HOST,
                diagnostic.remoteCandidateKind,
            )
            assertFalse(diagnostic.directRoutingPermitted)
            assertDiagnosticExcludes(
                diagnostic,
                androidId.toString(),
                pcId.toString(),
                snapshot.attemptId.toString(),
                "192.168.1.10",
                "192.168.1.20",
                "50001",
                "pc-key-epoch-3",
                "SENTINEL_TERMINAL_INPUT_OUTPUT",
            )
            assertEquals(listOf("gather", "mark", "nominate", "sign"), native.actions)
            assertEquals(101L, native.gatherRequest?.networkHandle)
            assertEquals(101L, native.nominationNetworkHandle)
            assertFalse(native.localProbeKey.contentEquals(native.peerProbeKey))
            assertEquals(1, signaling.offers)
            assertEquals(1, signaling.nominations)
            assertEquals(41, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
            assertTrue(native.staleEventsObserved >= 2)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
        assertEquals(1, native.cancelCalls)
        assertEquals(1, native.closeCalls)
    }

    @Test
    fun configRejectionAndSignalingQueueOverflowFaultOnlyDirect() = runTest {
        val relay = FakeRelay().also { it.connect(9) }
        val rejectedIdentity = DeviceIdentity.generate(FixedSecureRandom())
        val rejected = FakeSignaling(
            now,
            androidId,
            pcId,
            pairwise,
            config = config(directRoutingPermitted = true),
        )
        val rejectedRuntime = runtime(this, rejectedIdentity, rejected, FakeNative(), FakePlatform())
        val rejectedPeer = peer()
        try {
            arm(rejectedRuntime, rejectedPeer, relay.generation)
            advanceTimeBy(50)
            runCurrent()
            assertEquals(ProductionDirectShadowState.RELAY_ONLY, rejectedRuntime.snapshot.value.state)
            assertEquals("DIRECT_CONFIG_REJECTED", rejectedRuntime.snapshot.value.reasonCode)
            assertFalse(rejectedRuntime.snapshot.value.directRoutingPermitted)
            assertEquals(9, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            rejectedPeer.close()
            rejectedRuntime.closeAndJoin()
            rejectedIdentity.destroy()
        }

        val overflowIdentity = DeviceIdentity.generate(FixedSecureRandom())
        val overflowSignaling = FakeSignaling(now, androidId, pcId, pairwise).also { fake ->
            repeat(40) { fake.enqueue(TransportSignalEnvelopeV1.getDefaultInstance()) }
        }
        val overflowNative = FakeNative()
        val overflowRuntime = runtime(this, overflowIdentity, overflowSignaling, overflowNative, FakePlatform())
        val overflowPeer = peer()
        try {
            arm(overflowRuntime, overflowPeer, relay.generation)
            advanceTimeBy(250)
            runCurrent()
            assertEquals(ProductionDirectShadowState.RELAY_ONLY, overflowRuntime.snapshot.value.state)
            assertEquals("DIRECT_SIGNAL_QUEUE_OVERFLOW", overflowRuntime.snapshot.value.reasonCode)
            assertFalse(overflowRuntime.snapshot.value.directRoutingPermitted)
            assertEquals(9, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            overflowPeer.close()
            overflowRuntime.closeAndJoin()
            overflowIdentity.destroy()
        }
    }

    @Test
    fun sameLanCandidateSelectionNominatesHostPairBeforeReflexivePair() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val remoteCandidates = remoteCandidates("192.168.1.20", "198.51.100.20")
        val native = FakeNative(localReflexiveAddress = "203.0.113.10")
        val signaling = FakeSignaling(
            now,
            androidId,
            pcId,
            pairwise,
            answerCandidates = remoteCandidates,
        )
        val runtime = runtime(this, identity, signaling, native, FakePlatform())
        val relay = FakeRelay().also { it.connect(61) }
        val callerPeer = peer()
        try {
            arm(runtime, callerPeer, relay.generation)
            advanceTimeBy(250)
            runCurrent()

            assertEquals(ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, runtime.snapshot.value.state)
            assertEquals(
                DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
                signaling.offeredCandidates.single {
                    it.candidateId.toByteArray().contentEquals(native.nominatedLocalCandidateId)
                }.kind,
            )
            assertEquals(
                DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST,
                remoteCandidates.single {
                    it.candidateId.toByteArray().contentEquals(native.nominatedRemoteCandidateId)
                }.kind,
            )
            assertEquals("192.168.1.20", InetAddress.getByAddress(native.nominatedRemoteAddress).hostAddress)
            assertFalse(runtime.directRoutingPermitted)
            assertEquals(61, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun differentLanCandidateSelectionNominatesReflexivePairForDoubleNat() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val remoteCandidates = remoteCandidates("192.168.2.20", "198.51.100.20")
        val native = FakeNative(localReflexiveAddress = "203.0.113.10")
        val signaling = FakeSignaling(
            now,
            androidId,
            pcId,
            pairwise,
            answerCandidates = remoteCandidates,
        )
        val runtime = runtime(this, identity, signaling, native, FakePlatform())
        val relay = FakeRelay().also { it.connect(62) }
        val callerPeer = peer()
        try {
            arm(runtime, callerPeer, relay.generation)
            advanceTimeBy(250)
            runCurrent()

            assertEquals(ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, runtime.snapshot.value.state)
            assertEquals(
                DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
                signaling.offeredCandidates.single {
                    it.candidateId.toByteArray().contentEquals(native.nominatedLocalCandidateId)
                }.kind,
            )
            assertEquals(
                DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
                remoteCandidates.single {
                    it.candidateId.toByteArray().contentEquals(native.nominatedRemoteCandidateId)
                }.kind,
            )
            assertEquals("198.51.100.20", InetAddress.getByAddress(native.nominatedRemoteAddress).hostAddress)
            assertFalse(runtime.directRoutingPermitted)
            assertEquals(62, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun incompatibleCandidateFamiliesFailClosedWithoutTouchingRelay() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val native = FakeNative()
        val signaling = FakeSignaling(
            now,
            androidId,
            pcId,
            pairwise,
            answerCandidates = remoteCandidates("fd00:2::20"),
        )
        val runtime = runtime(this, identity, signaling, native, FakePlatform())
        val relay = FakeRelay().also { it.connect(63) }
        val callerPeer = peer()
        try {
            arm(runtime, callerPeer, relay.generation)
            advanceTimeBy(250)
            runCurrent()

            assertEquals(ProductionDirectShadowState.RELAY_ONLY, runtime.snapshot.value.state)
            assertEquals("DIRECT_CANDIDATE_PAIR_UNAVAILABLE", runtime.snapshot.value.reasonCode)
            assertFalse("nominate" in native.actions)
            assertEquals(0, signaling.nominations)
            assertFalse(runtime.directRoutingPermitted)
            assertEquals(63, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun unrelatedHostSubnetsWithoutReflexiveCandidatesFailClosed() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val native = FakeNative()
        val signaling = FakeSignaling(
            now,
            androidId,
            pcId,
            pairwise,
            answerCandidates = remoteCandidates("192.168.2.20"),
        )
        val runtime = runtime(this, identity, signaling, native, FakePlatform())
        val relay = FakeRelay().also { it.connect(64) }
        val callerPeer = peer()
        try {
            arm(runtime, callerPeer, relay.generation)
            advanceTimeBy(250)
            runCurrent()

            assertEquals(ProductionDirectShadowState.RELAY_ONLY, runtime.snapshot.value.state)
            assertEquals("DIRECT_CANDIDATE_PAIR_UNAVAILABLE", runtime.snapshot.value.reasonCode)
            assertFalse("nominate" in native.actions)
            assertEquals(64, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun networkRelayAndAuthenticationReplacementRetireOnlyExactAttempt() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val native = FakeNative(holdBeforeShadowReady = true)
        val signaling = FakeSignaling(now, androidId, pcId, pairwise)
        val runtime = runtime(this, identity, signaling, native, FakePlatform())
        val relay = FakeRelay().also { it.connect(77) }
        val peer = peer()
        val stalePeer = peer()
        try {
            arm(runtime, peer, relay.generation)
            advanceTimeBy(100)
            runCurrent()
            assertTrue(native.nominationNetworkHandle != 0L)

            assertFalse(runtime.observeCapturedNetwork(AUTH_GENERATION + 1, network(8, 202L)))
            assertFalse(runtime.replaceAuthorizedPeers(AUTH_GENERATION + 1, listOf(stalePeer)))
            assertFalse(runtime.observeRelayConnected(AUTH_GENERATION + 1, 0))
            assertEquals(0, native.networkChangedCalls)
            assertEquals(
                "snapshot=${runtime.snapshot.value} actions=${native.actions}",
                0,
                native.cancelCalls,
            )

            assertTrue(runtime.observeCapturedNetwork(AUTH_GENERATION, network(1, 202L)))
            assertEquals("DIRECT_NETWORK_CHANGED", runtime.diagnostic.value.reasonCode)
            assertFalse(runtime.diagnostic.value.directRoutingPermitted)
            advanceTimeBy(100)
            runCurrent()
            assertEquals(1, native.networkChangedCalls)
            assertEquals(
                "snapshot=${runtime.snapshot.value} actions=${native.actions}",
                0,
                native.cancelCalls,
            )
            assertEquals(77, relay.generation)
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
            assertEquals(ProductionDirectShadowState.NEGOTIATING, runtime.snapshot.value.state)
            assertEquals(202L, native.nominationNetworkHandle)
            assertFalse(runtime.snapshot.value.directRoutingPermitted)

            assertTrue(runtime.observeRelayConnected(AUTH_GENERATION, 0))
            assertEquals(ProductionDirectShadowState.WAITING_FOR_RELAY, runtime.snapshot.value.state)
            assertEquals(ProductionDirectShadowState.WAITING_FOR_RELAY, runtime.diagnostic.value.state)
            assertEquals("RELAY_UNAVAILABLE", runtime.diagnostic.value.reasonCode)
            assertEquals(0, runtime.diagnostic.value.relayGeneration)
            assertFalse(runtime.diagnostic.value.directRoutingPermitted)
            relay.disconnectExternally()
            assertEquals(0, relay.disconnectCalls)
            assertEquals(0, relay.reconnectCalls)
        } finally {
            peer.close()
            stalePeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
        assertEquals(2, native.closeCalls)
    }

    @Test
    fun positiveRelayGenerationReplacementRetiresAndRebuildsTheExactAttempt() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val firstNative = FakeNative(holdBeforeShadowReady = true)
        val secondNative = FakeNative(holdBeforeShadowReady = true)
        val signaling = FakeSignaling(now, androidId, pcId, pairwise)
        var nativeCalls = 0
        val runtime = ProductionDirectShadowRuntime(
            enabled = true,
            authenticationGeneration = AUTH_GENERATION,
            localDeviceId = androidId,
            identity = identity,
            relayEvents = emptyFlow<ProductionRealtimeSessionConnectionEvent>(),
            signalingFactory = { _ -> signaling },
            nativeFactory = { if (nativeCalls++ == 0) firstNative else secondNative },
            platform = FakePlatform(),
            parentScope = this,
            clockMillis = { now },
            random = FixedSecureRandom(),
            pollDelay = ProductionDirectShadowDelay { kotlinx.coroutines.delay(it) },
            retryDelay = ProductionDirectShadowDelay { kotlinx.coroutines.awaitCancellation() },
            uuidSource = DeterministicUuids(),
        )
        val callerPeer = peer()
        try {
            arm(runtime, callerPeer, relayGeneration = 77)
            advanceTimeBy(100)
            runCurrent()
            assertTrue(firstNative.nominationNetworkHandle != 0L)

            assertTrue(runtime.observeRelayConnected(AUTH_GENERATION, 78))
            assertEquals(78, runtime.snapshot.value.relayGeneration)
            assertEquals(0L, runtime.snapshot.value.pathEpoch)
            assertEquals(null, runtime.snapshot.value.attemptId)
            assertEquals(1, firstNative.cancelCalls)
            assertEquals(1, firstNative.closeCalls)

            advanceTimeBy(100)
            runCurrent()
            assertEquals(ProductionDirectShadowState.NEGOTIATING, runtime.snapshot.value.state)
            assertEquals(78, runtime.snapshot.value.relayGeneration)
            assertTrue(secondNative.nominationNetworkHandle != 0L)
            assertEquals(2, nativeCalls)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun closeAndJoinCancelsAStalledPlatformPreparationAndClosesItsOwnedLane() = runTest {
        val identity = DeviceIdentity.generate(FixedSecureRandom())
        val signaling = FakeSignaling(now, androidId, pcId, pairwise)
        val entered = CompletableDeferred<Unit>()
        var platformCloseCalls = 0
        val platform = object : ProductionDirectShadowPlatform {
            override fun capture(network: android.net.Network): CapturedDirectNetwork? = null

            override suspend fun prepare(
                network: CapturedDirectNetwork,
                stunServers: List<String>,
            ): PreparedDirectGather {
                entered.complete(Unit)
                awaitCancellation()
            }

            override fun close() {
                platformCloseCalls++
            }
        }
        val runtime = ProductionDirectShadowRuntime(
            enabled = true,
            authenticationGeneration = AUTH_GENERATION,
            localDeviceId = androidId,
            identity = identity,
            relayEvents = emptyFlow<ProductionRealtimeSessionConnectionEvent>(),
            signalingFactory = { _ -> signaling },
            nativeFactory = { FakeNative() },
            platform = platform,
            parentScope = this,
            clockMillis = { now },
            random = FixedSecureRandom(),
            pollDelay = ProductionDirectShadowDelay { kotlinx.coroutines.delay(it) },
            retryDelay = ProductionDirectShadowDelay { kotlinx.coroutines.awaitCancellation() },
            uuidSource = DeterministicUuids(),
        )
        val callerPeer = peer()
        try {
            arm(runtime, callerPeer, relayGeneration = 91)
            entered.await()
            runtime.closeAndJoin()
            assertEquals(1, platformCloseCalls)
            assertEquals(ProductionDirectShadowState.STOPPED, runtime.snapshot.value.state)
        } finally {
            callerPeer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
        assertEquals(1, platformCloseCalls)
    }

    @Test
    fun nativeUdpSignalingAndCursorFaultsStayRelayOnly() = runTest {
        val relay = FakeRelay().also { it.connect(88) }
        val cases = listOf(
            Triple(FakeSignaling(now, androidId, pcId, pairwise), FakeNative(gatherFails = true),
                "DIRECT_NATIVE_GATHER_FAILED"),
            Triple(FakeSignaling(now, androidId, pcId, pairwise, acceptOffer = false), FakeNative(),
                "DIRECT_OFFER_SEND_FAILED"),
            Triple(FakeSignaling(now, androidId, pcId, pairwise, answerSequence = 2), FakeNative(),
                "DIRECT_SIGNAL_CURSOR_REJECTED"),
        )
        for ((signaling, native, expectedReason) in cases) {
            val identity = DeviceIdentity.generate(FixedSecureRandom())
            val runtime = runtime(this, identity, signaling, native, FakePlatform())
            val peer = peer()
            try {
                arm(runtime, peer, relay.generation)
                advanceTimeBy(250)
                runCurrent()
                assertEquals(ProductionDirectShadowState.RELAY_ONLY, runtime.snapshot.value.state)
                assertEquals(expectedReason, runtime.snapshot.value.reasonCode)
                assertFalse(runtime.snapshot.value.directRoutingPermitted)
                assertEquals(88, relay.generation)
                assertEquals(0, relay.disconnectCalls)
                assertEquals(0, relay.reconnectCalls)
            } finally {
                peer.close()
                runtime.closeAndJoin()
                identity.destroy()
            }
        }
    }

    @Test
    fun nominationCommandPreservesDirectionalKeysAndSharedConnectivityNonce() {
        val command = nominationCommand()
        try {
            assertFalse(command.localProbeHmacKey.contentEquals(command.peerProbeHmacKey))
            assertTrue(command.request.localSendProbeHmacKey.contentEquals(command.localProbeHmacKey))
            assertTrue(command.request.peerReceiveProbeHmacKey.contentEquals(command.peerProbeHmacKey))
            assertTrue(command.request.localProbeNonce.contentEquals(command.request.peerProbeNonce))
        } finally {
            command.destroy()
            command.request.localSendProbeHmacKey.fill(0)
            command.request.peerReceiveProbeHmacKey.fill(0)
        }
    }

    @Test
    fun timeoutAndFreeTextFailurePublishOnlyAllowlistedDiagnostics() = runTest {
        val timeoutIdentity = DeviceIdentity.generate(FixedSecureRandom())
        val timeoutNative = FakeNative(holdBeforeShadowReady = true)
        val timeoutRuntime = runtime(
            this,
            timeoutIdentity,
            FakeSignaling(now, androidId, pcId, pairwise),
            timeoutNative,
            FakePlatform(),
        )
        val timeoutPeer = peer()
        try {
            arm(timeoutRuntime, timeoutPeer, 101)
            advanceTimeBy(12_100)
            runCurrent()

            val diagnostic = timeoutRuntime.diagnostic.value
            assertEquals(ProductionDirectShadowState.RELAY_ONLY, diagnostic.state)
            assertEquals("DIRECT_ATTEMPT_TIMEOUT", diagnostic.reasonCode)
            assertFalse(diagnostic.directRoutingPermitted)
        } finally {
            timeoutPeer.close()
            timeoutRuntime.closeAndJoin()
            timeoutIdentity.destroy()
        }

        val exceptionSentinel =
            "SENTINEL_ACCOUNT_DEVICE_ATTEMPT_198.51.100.77:54321_KEY_TOKEN_SIGNATURE_BODY"
        val failureIdentity = DeviceIdentity.generate(FixedSecureRandom())
        val failureRuntime = runtime(
            this,
            failureIdentity,
            FakeSignaling(now, androidId, pcId, pairwise),
            FakeNative(),
            FakePlatform(exceptionSentinel),
        )
        val failurePeer = peer()
        try {
            arm(failureRuntime, failurePeer, 102)
            advanceTimeBy(50)
            runCurrent()

            val diagnostic = failureRuntime.diagnostic.value
            assertEquals(ProductionDirectShadowState.RELAY_ONLY, diagnostic.state)
            assertEquals("DIRECT_SHADOW_FAILED", diagnostic.reasonCode)
            assertDiagnosticExcludes(diagnostic, exceptionSentinel)
            assertFalse(diagnostic.directRoutingPermitted)
        } finally {
            failurePeer.close()
            failureRuntime.closeAndJoin()
            failureIdentity.destroy()
        }
    }

    @Test
    fun unknownDiagnosticReasonIsNormalizedToFixedFallback() {
        val sentinel = "SENTINEL_FREE_TEXT_198.51.100.88:60000"
        assertEquals(
            DIRECT_SHADOW_DIAGNOSTIC_REASON_REDACTED,
            normalizeProductionDirectShadowDiagnosticReason(sentinel),
        )
        assertEquals(
            "DIRECT_NETWORK_CHANGED",
            normalizeProductionDirectShadowDiagnosticReason("DIRECT_NETWORK_CHANGED"),
        )
        assertEquals("NONE", normalizeProductionDirectShadowDiagnosticReason(""))
    }

    @Test
    fun diagnosticLogLineContainsOnlyTheRedactedPublicContract() {
        val diagnostic = ProductionDirectShadowDiagnostic(
            state = ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED,
            reasonCode = "NONE",
            authenticationGeneration = 8,
            relayGeneration = 5,
            networkGeneration = 3,
            pathEpoch = 42,
            selectedPairRank = 2,
            localCandidateKind = ProductionDirectShadowDiagnosticCandidateKind.HOST,
            remoteCandidateKind = ProductionDirectShadowDiagnosticCandidateKind.SRFLX,
        )

        assertEquals(
            "state=SHADOW_READY_NOT_ROUTED reason_code=NONE authentication_generation=8 " +
                "relay_generation=5 network_generation=3 path_epoch=42 selected_pair_rank=2 " +
                "local_candidate_type=HOST remote_candidate_type=SRFLX " +
                "direct_routing_permitted=false",
            diagnostic.toLogLine(),
        )
        assertDiagnosticExcludes(
            diagnostic,
            androidId.toString(),
            pcId.toString(),
            "192.168.1.10",
            "50001",
            "SENTINEL_TERMINAL_INPUT_OUTPUT",
        )
    }

    @Test
    fun diagnosticSchemaContainsOnlyReviewedRedactedFields() {
        val fields = ProductionDirectShadowDiagnostic::class.java.declaredFields
            .filterNot { it.isSynthetic || Modifier.isStatic(it.modifiers) }
            .mapTo(sortedSetOf()) { it.name }
        assertEquals(
            sortedSetOf(
                "state",
                "reasonCode",
                "authenticationGeneration",
                "relayGeneration",
                "networkGeneration",
                "pathEpoch",
                "selectedPairRank",
                "localCandidateKind",
                "remoteCandidateKind",
            ),
            fields,
        )
    }

    private fun assertDiagnosticExcludes(
        diagnostic: ProductionDirectShadowDiagnostic,
        vararg sentinels: String,
    ) {
        val rendered = diagnostic.toString()
        for (sentinel in sentinels) {
            assertFalse("diagnostic leaked sentinel: $sentinel in $rendered", rendered.contains(sentinel))
        }
    }

    private fun runtime(
        parentScope: kotlinx.coroutines.CoroutineScope,
        identity: DeviceIdentity,
        signaling: FakeSignaling,
        native: FakeNative,
        platform: FakePlatform,
    ) = ProductionDirectShadowRuntime(
        enabled = true,
        authenticationGeneration = AUTH_GENERATION,
        localDeviceId = androidId,
        identity = identity,
        relayEvents = emptyFlow<ProductionRealtimeSessionConnectionEvent>(),
        signalingFactory = { _ -> signaling },
        nativeFactory = { native },
        platform = platform,
        parentScope = parentScope,
        clockMillis = { now },
        random = FixedSecureRandom(),
        pollDelay = ProductionDirectShadowDelay { kotlinx.coroutines.delay(it) },
        retryDelay = ProductionDirectShadowDelay { kotlinx.coroutines.awaitCancellation() },
        uuidSource = DeterministicUuids(),
    )

    private fun arm(
        runtime: ProductionDirectShadowRuntime,
        callerPeer: ProductionDirectAuthorizedPeer,
        relayGeneration: Int,
    ) {
        assertTrue(runtime.start())
        assertTrue(runtime.replaceAuthorizedPeers(AUTH_GENERATION, listOf(callerPeer)))
        assertTrue(runtime.observeCapturedNetwork(AUTH_GENERATION, network(1, 101L)))
        assertTrue(runtime.observeRelayConnected(AUTH_GENERATION, relayGeneration))
    }

    private fun peer() = ProductionDirectAuthorizedPeer(
        deviceId = pcId,
        keyEpoch = 3,
        keyId = "pc-key-epoch-3",
        pairwiseKey = pairwise,
        signingPublicKey = ByteArray(32) { (0x40 + it).toByte() },
    )

    private fun network(index: Int, handle: Long) = CapturedDirectNetwork(
        token = "network-$index",
        handle = handle,
        advertisedAddress = InetAddress.getByName("192.168.$index.10"),
    )

    private fun config(directRoutingPermitted: Boolean = false) = DirectTransportServerConfig(
        protocolVersion = 1,
        capabilities = setOf("direct_signaling_v1", "direct_transport_shadow_v1"),
        directRoutingPermitted = directRoutingPermitted,
        ticketSigningKeys = mapOf(
            "ticket-key" to DirectTicketSigningKey("ticket-key", ByteArray(32) { 0x31 }),
        ),
        stunServers = listOf("stun:one.example:3478"),
    )

    private fun remoteCandidates(
        hostAddress: String,
        reflexiveAddress: String? = null,
    ): List<DirectCandidateV1> {
        val hostId = ByteArray(16) { 0x55 }
        val host = DirectCandidateV1.newBuilder()
            .setCandidateId(ByteString.copyFrom(hostId))
            .setKind(DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST)
            .setCarrier(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
            .setIpAddress(ByteString.copyFrom(InetAddress.getByName(hostAddress).address))
            .setPort(50_002)
            .setPriority(1_900_000_000)
            .setNetworkCost(0)
            .setExpiresAtMs(now + 120_000)
            .build()
        val reflexive = reflexiveAddress?.let { address ->
            DirectCandidateV1.newBuilder()
                .setCandidateId(ByteString.copyFrom(ByteArray(16) { 0x56 }))
                .setKind(DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE)
                .setCarrier(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
                .setIpAddress(ByteString.copyFrom(InetAddress.getByName(address).address))
                .setPort(50_002)
                .setPriority(1_800_000_000)
                .setRelatedCandidateId(ByteString.copyFrom(hostId))
                .setNetworkCost(0)
                .setExpiresAtMs(now + 120_000)
                .build()
        }
        hostId.fill(0)
        return listOfNotNull(host, reflexive)
    }

    private fun ticket(connectionId: UUID) = DirectPathTicketV1.newBuilder()
        .setFormatVersion(1)
        .setTicketId(ByteString.copyFrom(ByteArray(16) { 0x11 }))
        .setAccountId(ByteString.copyFrom(ByteArray(16) { 0x12 }))
        .setInitiatorDeviceId(ByteString.copyFrom(uuidBytes(androidId)))
        .setResponderDeviceId(ByteString.copyFrom(uuidBytes(pcId)))
        .setConnectionId(ByteString.copyFrom(uuidBytes(connectionId)))
        .setPathEpoch(31)
        .setIssuedAtMs(now)
        .setExpiresAtMs(now + 120_000)
        .addAllowedCarriers(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
        .setServerNonce(ByteString.copyFrom(ByteArray(16) { 0x13 }))
        .setServerSigningKeyId(ByteString.copyFromUtf8("ticket-key"))
        .setServerSignature(ByteString.copyFrom(ByteArray(64) { 0x14 }))
        .build()

    private inner class FakeSignaling(
        private val clock: Long,
        private val localId: UUID,
        private val remoteId: UUID,
        private val key: ByteArray,
        private val config: DirectTransportServerConfig = config(),
        private val acceptOffer: Boolean = true,
        private val answerSequence: Long = 1,
        private val answerCandidates: List<DirectCandidateV1>? = null,
    ) : ProductionDirectShadowSignaling {
        private val channel = Channel<TransportSignalEnvelopeV1>(Channel.UNLIMITED)
        private val mutableState = MutableStateFlow<DirectTransportSignalingState>(
            DirectTransportSignalingState.Idle,
        )
        private var connectionId: UUID? = null
        private var issuedTicket: DirectPathTicketV1? = null
        var offers = 0
        var nominations = 0
        var closed = false
        var offeredCandidates: List<DirectCandidateV1> = emptyList()

        override val incoming: Flow<TransportSignalEnvelopeV1> = channel.receiveAsFlow()
        override val state: StateFlow<DirectTransportSignalingState> = mutableState
        override suspend fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig> =
            DirectSignalingCallResult.Success(config)

        override fun start(): Boolean {
            mutableState.value = DirectTransportSignalingState.Connecting(1)
            return true
        }

        override suspend fun issueTicket(
            targetDeviceId: UUID,
            connectionId: UUID,
            config: DirectTransportServerConfig,
        ): DirectSignalingCallResult<ValidatedDirectPathTicket> {
            assertEquals(remoteId, targetDeviceId)
            this.connectionId = connectionId
            val ticket = ticket(connectionId)
            issuedTicket = ticket
            return DirectSignalingCallResult.Success(
                ValidatedDirectPathTicket(ticket, ticket.toByteArray()),
            )
        }

        override fun send(envelope: TransportSignalEnvelopeV1): Boolean {
            when (envelope.signalType) {
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_OFFER -> {
                    offers++
                    if (!acceptOffer) return false
                    val plaintext = DirectTransportCrypto.decryptSignal(key, envelope)
                    try {
                        val offer = TransportSignalPayloadV1.parseFrom(plaintext)
                        assertTrue(offer.hasPathOffer())
                        offeredCandidates = offer.pathOffer.candidatesList
                        enqueue(answer(envelope, offer.pathOffer.ticket))
                    } finally {
                        plaintext.fill(0)
                    }
                }
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION -> nominations++
                else -> return false
            }
            return true
        }

        fun enqueue(value: TransportSignalEnvelopeV1) {
            assertTrue(channel.trySend(value).isSuccess)
        }

        private fun answer(
            offerEnvelope: TransportSignalEnvelopeV1,
            ticket: DirectPathTicketV1,
        ): TransportSignalEnvelopeV1 {
            val answer = DirectPathAnswerV1.newBuilder()
                .setTicketId(ticket.ticketId)
                .setQuicCertificateSha256(ByteString.copyFrom(ByteArray(32) { 0x66 }))
                .addAllCandidates(answerCandidates ?: remoteCandidates("192.168.1.20"))
                .setGatheringComplete(true)
                .setResult(DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED)
                .setReasonCode("")
                .build()
            val payload = TransportSignalPayloadV1.newBuilder()
                .setFormatVersion(1)
                .setPathAnswer(answer)
                .build()
            val metadata = TransportSignalEnvelopeV1.newBuilder()
                .setProtocolVersion(1)
                .setSignalType(TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_ANSWER)
                .setConnectionId(offerEnvelope.connectionId)
                .setSenderDeviceId(ByteString.copyFrom(uuidBytes(remoteId)))
                .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(localId)))
                .setPathEpoch(ticket.pathEpoch)
                .setSignalSeq(answerSequence)
                .setSignalAck(offerEnvelope.signalSeq)
                .setSentAtMs(clock)
                .setKeyId(offerEnvelope.keyId)
                .build()
            val plaintext = payload.toByteArray()
            return try {
                DirectTransportCrypto.encryptSignal(key, metadata, plaintext)
            } finally {
                plaintext.fill(0)
            }
        }

        override fun close() {
            closed = true
            mutableState.value = DirectTransportSignalingState.Closed
        }
    }

    private class FakePlatform(
        private val failureMessage: String? = null,
    ) : ProductionDirectShadowPlatform {
        override fun capture(network: android.net.Network): CapturedDirectNetwork? = null

        override suspend fun prepare(
            network: CapturedDirectNetwork,
            stunServers: List<String>,
        ): PreparedDirectGather {
            failureMessage?.let { throw IllegalStateException(it) }
            assertTrue(network.handle > 0)
            assertEquals(listOf("stun:one.example:3478"), stunServers)
            return PreparedDirectGather(
                networkHandle = network.handle,
                localBind = DirectNativeSocketAddress(ByteArray(4), 0),
                advertisedAddress = network.advertisedAddress.address,
                stunServers = listOf(DirectNativeSocketAddress(byteArrayOf(1, 1, 1, 1), 3478)),
            )
        }
    }

    private class FakeNative(
        private val includeStaleEvents: Boolean = false,
        private val holdBeforeShadowReady: Boolean = false,
        private val gatherFails: Boolean = false,
        private val localReflexiveAddress: String? = null,
    ) : ProductionDirectShadowNative {
        private val events = ArrayDeque<DirectNativeEvent>()
        val actions = mutableListOf<String>()
        var gatherRequest: DirectNativeGatherRequest? = null
        var nominationNetworkHandle = 0L
        var localProbeKey = ByteArray(0)
        var peerProbeKey = ByteArray(0)
        var nominatedLocalCandidateId = ByteArray(0)
        var nominatedRemoteCandidateId = ByteArray(0)
        var nominatedRemoteAddress = ByteArray(0)
        var staleEventsObserved = 0
        var cancelCalls = 0
        var networkChangedCalls = 0
        var closeCalls = 0

        override fun gather(request: DirectNativeGatherRequest): DirectNativeResult<Unit> {
            actions += "gather"
            gatherRequest = request
            if (gatherFails) {
                return DirectNativeResult.Failure(
                    DirectNativeEvent.relayOnly(
                        DirectNativeSyntheticReason.FFI_CALL_FAILED,
                        request.epoch,
                        request.attemptId,
                    ),
                )
            }
            if (includeStaleEvents) {
                events += event(
                    DirectNativeEventKind.IDENTITY_READY,
                    request.epoch - 1,
                    ByteArray(16) { 0x7f },
                    1,
                    payload = ByteArray(64) { 0x21 },
                )
                staleEventsObserved++
            }
            events += event(
                DirectNativeEventKind.IDENTITY_READY,
                request.epoch,
                request.attemptId,
                2,
                payload = ByteArray(128) { 0x22 },
            )
            val endpoint = DirectNativeSocketAddress(request.advertisedLocalIp, 50_001)
            val candidates = buildList {
                add(endpoint.ownedCopy())
                localReflexiveAddress?.let { address ->
                    add(DirectNativeSocketAddress(InetAddress.getByName(address).address, 50_001))
                }
            }
            if (includeStaleEvents) {
                events += event(
                    DirectNativeEventKind.IGNORED_STALE,
                    request.epoch,
                    ByteArray(16) { 0x6f },
                    3,
                )
                staleEventsObserved++
            }
            events += event(
                DirectNativeEventKind.CANDIDATES_READY,
                request.epoch,
                request.attemptId,
                4,
                endpoint = endpoint,
                payload = ByteArray(80),
                candidates = candidates,
            )
            return DirectNativeResult.Success(Unit)
        }

        override fun markCandidatesExchanged(
            epoch: Long,
            attemptId: ByteArray,
            generation: Long,
            candidates: List<DirectNativeSocketAddress>,
        ): DirectNativeResult<Unit> {
            actions += "mark"
            assertEquals(gatherRequest?.epoch, epoch)
            assertTrue(gatherRequest?.attemptId?.contentEquals(attemptId) == true)
            assertEquals(4L, generation)
            assertEquals(if (localReflexiveAddress == null) 1 else 2, candidates.size)
            return DirectNativeResult.Success(Unit)
        }

        override fun nominate(command: ProductionDirectNominationCommand): DirectNativeResult<Unit> {
            actions += "nominate"
            nominationNetworkHandle = command.request.networkHandle
            localProbeKey = command.localProbeHmacKey.copyOf()
            peerProbeKey = command.peerProbeHmacKey.copyOf()
            nominatedLocalCandidateId = command.request.initiatorCandidateId.copyOf()
            nominatedRemoteCandidateId = command.request.responderCandidateId.copyOf()
            nominatedRemoteAddress = command.request.remoteCandidate.ipAddress.copyOf()
            assertTrue(command.request.localSendProbeHmacKey.contentEquals(command.localProbeHmacKey))
            assertTrue(command.request.peerReceiveProbeHmacKey.contentEquals(command.peerProbeHmacKey))
            assertTrue(command.request.localProbeNonce.contentEquals(command.request.peerProbeNonce))
            assertTrue(command.request.localProofNonce.size in 16..32)
            assertFalse(command.request.localProofNonce.contentEquals(command.request.localProbeNonce))
            assertNotEquals(
                command.localProbeHmacKey.toList(),
                command.peerProbeHmacKey.toList(),
            )
            if (!holdBeforeShadowReady) {
                if (includeStaleEvents) {
                    events += event(
                        DirectNativeEventKind.SIGN_REQUEST,
                        command.request.epoch - 1,
                        ByteArray(16) { 0x5f },
                        5,
                        requestId = 90,
                        payload = ByteArray(96) { 0x32 },
                    )
                    staleEventsObserved++
                }
                events += event(
                    DirectNativeEventKind.SIGN_REQUEST,
                    command.request.epoch,
                    command.request.attemptId,
                    6,
                    requestId = 91,
                    payload = ByteArray(96) { 0x33 },
                )
                events += event(
                    DirectNativeEventKind.SHADOW_READY,
                    command.request.epoch,
                    command.request.attemptId,
                    7,
                    endpoint = command.request.remoteCandidate.ownedCopy(),
                    payload = ByteArray(32) { 0x44 },
                )
            }
            return DirectNativeResult.Success(Unit)
        }

        override fun cancel(epoch: Long, attemptId: ByteArray): DirectNativeResult<Unit> {
            cancelCalls++
            return DirectNativeResult.Success(Unit)
        }

        override fun networkChanged(epoch: Long, attemptId: ByteArray): DirectNativeResult<Unit> {
            networkChangedCalls++
            return DirectNativeResult.Success(Unit)
        }

        override fun submitSignature(
            epoch: Long,
            attemptId: ByteArray,
            requestId: Long,
            signature: ByteArray,
        ): DirectNativeResult<Unit> {
            actions += "sign"
            assertEquals(91L, requestId)
            assertEquals(64, signature.size)
            return DirectNativeResult.Success(Unit)
        }

        override fun poll(): DirectNativeResult<DirectNativeEvent?> =
            DirectNativeResult.Success(events.removeFirstOrNull())

        override fun close() {
            closeCalls++
        }

        private fun event(
            kind: DirectNativeEventKind,
            epoch: Long,
            attempt: ByteArray,
            generation: Long,
            requestId: Long = 0,
            endpoint: DirectNativeSocketAddress? = null,
            payload: ByteArray = ByteArray(0),
            candidates: List<DirectNativeSocketAddress> = emptyList(),
        ) = DirectNativeEvent(
            kind = kind,
            state = when (kind) {
                DirectNativeEventKind.SIGN_REQUEST -> DirectNativePathState.AUTHENTICATING
                DirectNativeEventKind.SHADOW_READY -> DirectNativePathState.SHADOW_DIRECT
                DirectNativeEventKind.IGNORED_STALE -> DirectNativePathState.RELAY_ONLY
                else -> DirectNativePathState.GATHERING
            },
            reason = 0,
            epoch = epoch,
            generation = generation,
            requestId = requestId,
            attemptId = attempt.copyOf(),
            endpoint = endpoint,
            payload = payload,
            candidates = candidates,
        )
    }

    private fun nominationCommand(): ProductionDirectNominationCommand {
        val localProbeKey = ByteArray(32) { 9 }
        val peerProbeKey = ByteArray(32) { 10 }
        val request = DirectNativeNominateRequest(
            epoch = 1,
            attemptId = ByteArray(16) { 1 },
            connectionId = ByteArray(16) { 2 },
            localDeviceId = ByteArray(16) { 3 },
            peerDeviceId = ByteArray(16) { 4 },
            role = dev.termlink.network.direct.nativebridge.DirectNativeRole.INITIATOR,
            timeoutMillis = 1_000,
            networkHandle = 7,
            localBind = DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 1), 50_001),
            remoteCandidate = DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 2), 50_002),
            ticket = ByteArray(64) { 5 },
            serverSigningPublicKey = ByteArray(32) { 6 },
            peerCertificateSha256 = ByteArray(32) { 7 },
            peerDeviceSigningPublicKey = ByteArray(32) { 8 },
            localSendProbeHmacKey = localProbeKey,
            peerReceiveProbeHmacKey = peerProbeKey,
            ticketId = ByteArray(16) { 10 },
            nominationId = ByteArray(16) { 11 },
            initiatorCandidateId = ByteArray(16) { 12 },
            responderCandidateId = ByteArray(16) { 13 },
            localProbeNonce = ByteArray(16) { 14 },
            peerProbeNonce = ByteArray(16) { 14 },
            localProofNonce = ByteArray(32) { 16 },
            nowMillis = now,
        )
        return ProductionDirectNominationCommand(
            request,
            localProbeKey,
            peerProbeKey,
        )
    }

    private class FakeRelay {
        var generation = 0
        var disconnectCalls = 0
        var reconnectCalls = 0

        fun connect(value: Int) {
            generation = value
        }

        fun disconnectExternally() {
            generation = 0
        }
    }

    private class DeterministicUuids : () -> UUID {
        private val next = AtomicInteger(1)
        override fun invoke(): UUID = UUID.fromString(
            if (next.getAndIncrement() == 1) "30313233-3435-4637-b839-3a3b3c3d3e3f"
            else "40414243-4445-4647-a849-4a4b4c4d4e4f",
        )
    }

    private class FixedSecureRandom : SecureRandom() {
        private var seed = 1
        override fun nextBytes(bytes: ByteArray) {
            bytes.indices.forEach { index -> bytes[index] = (seed + index).toByte() }
            seed++
        }
    }

    private fun uuidBytes(value: UUID): ByteArray = ByteBuffer.allocate(16)
        .putLong(value.mostSignificantBits)
        .putLong(value.leastSignificantBits)
        .array()

    private companion object {
        const val AUTH_GENERATION = 5L
    }
}

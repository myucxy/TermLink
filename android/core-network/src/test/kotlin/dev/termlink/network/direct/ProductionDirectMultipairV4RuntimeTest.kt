package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.DirectTransportCrypto
import dev.termlink.network.direct.nativebridge.DirectNativeAppendCheckPairsRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeBeginChecksRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidateKindV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidatePairV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidateV4
import dev.termlink.network.direct.nativebridge.DirectNativeCommitNominationRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeEvent
import dev.termlink.network.direct.nativebridge.DirectNativeEventV4
import dev.termlink.network.direct.nativebridge.DirectNativeGatherRequest
import dev.termlink.network.direct.nativebridge.DirectNativeGatherRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeNominateRequest
import dev.termlink.network.direct.nativebridge.DirectNativePathState
import dev.termlink.network.direct.nativebridge.DirectNativePathStateV4
import dev.termlink.network.direct.nativebridge.DirectNativeResult
import dev.termlink.network.direct.nativebridge.DirectNativeResultV4
import dev.termlink.network.direct.nativebridge.DirectNativeSocketAddress
import dev.termlink.network.direct.nativebridge.DirectNativeSocketAddressV4
import dev.termlink.network.direct.nativebridge.DirectNativeEventKindV4
import dev.termlink.network.production.ProductionRealtimeSessionConnectionEvent
import dev.termlink.protocol.transport.v1.DirectCandidateKindV1
import dev.termlink.protocol.transport.v1.DirectCandidateV1
import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectNominationAckV2
import dev.termlink.protocol.transport.v1.DirectNominationResultV2
import dev.termlink.protocol.transport.v1.DirectPathAnswerV1
import dev.termlink.protocol.transport.v1.DirectPathResultV1
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalPayloadV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.ExperimentalCoroutinesApi
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
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class ProductionDirectMultipairV4RuntimeTest {
    private val now = 1_900_000_000_000L
    private val androidId = UUID.fromString("10111213-1415-4617-9819-1a1b1c1d1e1f")
    private val pcId = UUID.fromString("20212223-2425-4627-a829-2a2b2c2d2e2f")
    private val androidKeyId = "android-v4-key"
    private val pcKeyId = "pc-v4-key"
    private val pairwise = ByteArray(32) { (it + 1).toByte() }

    @Test
    fun sameLanHostStartsBeforeStunBlackholeCompletesAndRemainsShadowOnly() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(now, androidId, pcId, pairwise, remoteCandidates(1))
        val native = MultipairNative(now, delayedGatherCompletion = true, validatedRank = 1)
        val relayGeneration = 91
        val runtime = runtime(this, identity, signaling, native)
        val peer = peer()
        try {
            arm(runtime, peer, relayGeneration)
            advanceTimeBy(1_000)
            runCurrent()

            assertEquals(runtime.snapshot.value.toString(), ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, runtime.snapshot.value.state)
            assertEquals(relayGeneration, runtime.snapshot.value.relayGeneration)
            assertFalse(runtime.snapshot.value.directRoutingPermitted)
            val diagnostic = runtime.diagnostic.value
            assertEquals(ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, diagnostic.state)
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
            val diagnosticText = diagnostic.toString()
            listOf(
                androidId.toString(),
                pcId.toString(),
                runtime.snapshot.value.attemptId.toString(),
                "192.168.1.10",
                "192.168.1.20",
                "50010",
                androidKeyId,
                pcKeyId,
            ).forEach { sentinel ->
                assertFalse("diagnostic leaked sentinel: $sentinel in $diagnosticText", diagnosticText.contains(sentinel))
            }
            assertEquals(listOf(false), signaling.offerGatheringStates)
            assertTrue(signaling.candidateUpdates.last())
            assertTrue(native.beginBeforeGatherComplete)
            assertEquals(1, signaling.nominatedRank)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            assertEquals(1, signaling.closeSignals)
            identity.destroy()
        }
    }

    @Test
    fun firstFourPairsMayFailAndFifthValidatedPairWinsDeterministically() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(now, androidId, pcId, pairwise, remoteCandidates(3))
        val native = MultipairNative(now, localReflexiveCount = 2, validatedRank = 5)
        val runtime = runtime(this, identity, signaling, native)
        val peer = peer()
        try {
            arm(runtime, peer, 92)
            advanceTimeBy(1_000)
            runCurrent()

            assertEquals(runtime.snapshot.value.toString(), ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, runtime.snapshot.value.state)
            assertEquals(5, signaling.nominatedRank)
            assertTrue(native.submittedPairs.size >= 5)
            assertEquals(5, native.committedPair?.rank)
            assertEquals(92, runtime.snapshot.value.relayGeneration)
            assertEquals(5, runtime.diagnostic.value.selectedPairRank)
            assertEquals(
                diagnosticKind(native.committedPair!!.initiator.kind),
                runtime.diagnostic.value.localCandidateKind,
            )
            assertEquals(
                diagnosticKind(native.committedPair!!.responder.kind),
                runtime.diagnostic.value.remoteCandidateKind,
            )
            assertFalse(runtime.diagnostic.value.directRoutingPermitted)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun unsupportedCapabilityFallsBackToFrozenV3WithoutAdvertisingOrUsingV4() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(
            now, androidId, pcId, pairwise, remoteCandidates(1), multipairCapability = false,
        )
        val nativeV4 = MultipairNative(now)
        val nativeV3 = RejectingV3Native()
        val runtime = runtime(this, identity, signaling, nativeV4, nativeV3)
        val peer = peer()
        try {
            arm(runtime, peer, 93)
            advanceTimeBy(250)
            runCurrent()

            assertEquals(0, nativeV4.gatherCalls)
            assertEquals(1, nativeV4.closeCalls)
            assertEquals(1, nativeV3.gatherCalls)
            assertFalse(signaling.multipairV2Active)
            assertEquals(93, runtime.snapshot.value.relayGeneration)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun mismatchedNominationAckFailsOnlyDirectAndKeepsRelayGeneration() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(
            now, androidId, pcId, pairwise, remoteCandidates(1), mismatchAck = true,
        )
        val native = MultipairNative(now, validatedRank = 1)
        val runtime = runtime(this, identity, signaling, native)
        val peer = peer()
        try {
            arm(runtime, peer, 94)
            advanceTimeBy(500)
            runCurrent()

            assertEquals(ProductionDirectShadowState.RELAY_ONLY, runtime.snapshot.value.state)
            assertEquals("DIRECT_V4_NOMINATION_ACK_REJECTED", runtime.snapshot.value.reasonCode)
            assertEquals(94, runtime.snapshot.value.relayGeneration)
            assertEquals(ProductionDirectShadowState.RELAY_ONLY, runtime.diagnostic.value.state)
            assertEquals("DIRECT_V4_NOMINATION_ACK_REJECTED", runtime.diagnostic.value.reasonCode)
            assertFalse(runtime.diagnostic.value.directRoutingPermitted)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun nativeGenerationGapFailsOnlyDirectAndKeepsRelayGeneration() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(now, androidId, pcId, pairwise, remoteCandidates(1))
        val native = MultipairNative(now, generationStep = 2)
        val runtime = runtime(this, identity, signaling, native)
        val peer = peer()
        try {
            arm(runtime, peer, 95)
            advanceTimeBy(250)
            runCurrent()

            assertEquals(ProductionDirectShadowState.RELAY_ONLY, runtime.snapshot.value.state)
            assertEquals("DIRECT_NATIVE_V4_EVENT_STALE", runtime.snapshot.value.reasonCode)
            assertEquals(95, runtime.snapshot.value.relayGeneration)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun localReflexiveThenRemoteReflexiveKeepsSubmittedPrefixAndAppendsUnseenPairs() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(
            now, androidId, pcId, pairwise, remoteCandidates(0),
            answerGatheringComplete = false,
            remoteTrickleCandidates = remoteCandidates(1).drop(1),
            remoteTrickleAfterCheckBatch = 2,
        )
        val native = MultipairNative(
            now, delayedGatherCompletion = true, localReflexiveCount = 1, validatedRank = 1,
        )
        val runtime = runtime(this, identity, signaling, native)
        val peer = peer()
        try {
            arm(runtime, peer, 96)
            advanceTimeBy(1_000)
            runCurrent()

            assertEquals(ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, runtime.snapshot.value.state)
            assertEquals(listOf(1, 2, 3, 4), native.submittedPairs.map { it.rank })
            assertPairIds(native.submittedPairs[1], 0x42, 0x61)
            assertPairIds(native.submittedPairs[2], 0x42, 0x62)
            assertEquals(96, runtime.snapshot.value.relayGeneration)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    @Test
    fun remoteReflexiveThenLocalReflexiveKeepsSubmittedPrefixAndAppendsUnseenPairs() = runTest {
        val identity = DeviceIdentity.generate(FixedRandom())
        val signaling = MultipairSignaling(
            now, androidId, pcId, pairwise, remoteCandidates(1),
            answerGatheringComplete = false,
            remoteTrickleAfterCheckBatch = 2,
        )
        val native = MultipairNative(
            now, delayedGatherCompletion = true, localReflexiveCount = 1, validatedRank = 1,
        )
        val runtime = runtime(this, identity, signaling, native)
        val peer = peer()
        try {
            arm(runtime, peer, 97)
            advanceTimeBy(1_000)
            runCurrent()

            assertEquals(ProductionDirectShadowState.SHADOW_READY_NOT_ROUTED, runtime.snapshot.value.state)
            assertEquals(listOf(1, 2, 3, 4), native.submittedPairs.map { it.rank })
            assertPairIds(native.submittedPairs[1], 0x41, 0x62)
            assertPairIds(native.submittedPairs[2], 0x42, 0x62)
            assertEquals(97, runtime.snapshot.value.relayGeneration)
            assertEquals(0, signaling.relayMutations)
        } finally {
            peer.close()
            runtime.closeAndJoin()
            identity.destroy()
        }
    }

    private fun assertPairIds(pair: DirectNativeCandidatePairV4, initiator: Int, responder: Int) {
        assertEquals(initiator.toByte(), pair.initiator.candidateId.first())
        assertEquals(responder.toByte(), pair.responder.candidateId.first())
    }

    private fun diagnosticKind(
        value: DirectNativeCandidateKindV4,
    ): ProductionDirectShadowDiagnosticCandidateKind = when (value) {
        DirectNativeCandidateKindV4.HOST -> ProductionDirectShadowDiagnosticCandidateKind.HOST
        DirectNativeCandidateKindV4.SERVER_REFLEXIVE ->
            ProductionDirectShadowDiagnosticCandidateKind.SRFLX
    }

    private fun runtime(
        scope: kotlinx.coroutines.CoroutineScope,
        identity: DeviceIdentity,
        signaling: MultipairSignaling,
        nativeV4: MultipairNative,
        nativeV3: ProductionDirectShadowNative = RejectingV3Native(),
    ) = ProductionDirectShadowRuntime(
        enabled = true,
        authenticationGeneration = AUTH_GENERATION,
        localDeviceId = androidId,
        identity = identity,
        relayEvents = emptyFlow<ProductionRealtimeSessionConnectionEvent>(),
        signalingFactory = { available -> signaling.apply { nativeV4Available = available } },
        nativeFactory = { nativeV3 },
        nativeV4Factory = { nativeV4 },
        platform = FakePlatform(),
        parentScope = scope,
        clockMillis = { now },
        random = FixedRandom(),
        pollDelay = ProductionDirectShadowDelay { kotlinx.coroutines.delay(it) },
        retryDelay = ProductionDirectShadowDelay { kotlinx.coroutines.awaitCancellation() },
        uuidSource = DeterministicUuids(),
    )

    private fun arm(runtime: ProductionDirectShadowRuntime, peer: ProductionDirectAuthorizedPeer, relay: Int) {
        assertTrue(runtime.start())
        assertTrue(runtime.replaceAuthorizedPeers(AUTH_GENERATION, listOf(peer)))
        assertTrue(
            runtime.observeCapturedNetwork(
                AUTH_GENERATION,
                CapturedDirectNetwork("wifi", 701, InetAddress.getByName("192.168.1.10")),
            ),
        )
        assertTrue(runtime.observeRelayConnected(AUTH_GENERATION, relay))
    }

    private fun peer() = ProductionDirectAuthorizedPeer(
        pcId, 7, pcKeyId, pairwise, ByteArray(32) { (0x50 + it).toByte() },
    )

    private fun config(multipair: Boolean) = DirectTransportServerConfig(
        protocolVersion = 1,
        capabilities = buildSet {
            add(DirectTransportSignalingClient.DIRECT_SIGNALING_CAPABILITY)
            add(DirectTransportSignalingClient.DIRECT_SHADOW_CAPABILITY)
            if (multipair) add(DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY)
        },
        directRoutingPermitted = false,
        ticketSigningKeys = mapOf(
            "ticket-key" to DirectTicketSigningKey("ticket-key", ByteArray(32) { 0x31 }),
        ),
        stunServers = listOf("stun:one.example:3478"),
    )

    private fun ticket(connection: UUID) = DirectPathTicketV1.newBuilder()
        .setFormatVersion(1)
        .setTicketId(ByteString.copyFrom(ByteArray(16) { 0x11 }))
        .setAccountId(ByteString.copyFrom(ByteArray(16) { 0x12 }))
        .setInitiatorDeviceId(ByteString.copyFrom(androidId.bytes()))
        .setResponderDeviceId(ByteString.copyFrom(pcId.bytes()))
        .setConnectionId(ByteString.copyFrom(connection.bytes()))
        .setPathEpoch(71)
        .setIssuedAtMs(now)
        .setExpiresAtMs(now + 120_000)
        .addAllowedCarriers(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
        .setServerNonce(ByteString.copyFrom(ByteArray(16) { 0x13 }))
        .setServerSigningKeyId(ByteString.copyFromUtf8("ticket-key"))
        .setServerSignature(ByteString.copyFrom(ByteArray(64) { 0x14 }))
        .build()

    private fun remoteCandidates(reflexiveCount: Int): List<DirectCandidateV1> {
        val hostId = ByteArray(16) { 0x61 }
        val values = mutableListOf(
            signalCandidate(hostId, null, "192.168.1.20", 50_020, true, 2_000_000_000),
        )
        repeat(reflexiveCount) { index ->
            values += signalCandidate(
                ByteArray(16) { (0x62 + index).toByte() }, hostId,
                "198.51.100.${20 + index}", 51_020 + index, false, 1_900_000_000 - index,
            )
        }
        return values
    }

    private fun signalCandidate(
        id: ByteArray,
        related: ByteArray?,
        address: String,
        port: Int,
        host: Boolean,
        priority: Int,
    ) = DirectCandidateV1.newBuilder()
        .setCandidateId(ByteString.copyFrom(id))
        .setKind(
            if (host) DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_HOST
            else DirectCandidateKindV1.DIRECT_CANDIDATE_KIND_V1_SERVER_REFLEXIVE,
        )
        .setCarrier(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
        .setIpAddress(ByteString.copyFrom(InetAddress.getByName(address).address))
        .setPort(port)
        .setPriority(priority)
        .setRelatedCandidateId(related?.let(ByteString::copyFrom) ?: ByteString.EMPTY)
        .setNetworkCost(0)
        .setExpiresAtMs(now + 120_000)
        .build()

    private inner class MultipairSignaling(
        private val clock: Long,
        private val localId: UUID,
        private val remoteId: UUID,
        private val key: ByteArray,
        private val answerCandidates: List<DirectCandidateV1>,
        private val multipairCapability: Boolean = true,
        private val mismatchAck: Boolean = false,
        private val answerGatheringComplete: Boolean = true,
        private val remoteTrickleCandidates: List<DirectCandidateV1> = emptyList(),
        private val remoteTrickleAfterCheckBatch: Int = 0,
    ) : ProductionDirectShadowSignaling {
        private val channel = Channel<TransportSignalEnvelopeV1>(Channel.UNLIMITED)
        private val mutableState = MutableStateFlow<DirectTransportSignalingState>(
            DirectTransportSignalingState.Idle,
        )
        private lateinit var connection: UUID
        private lateinit var issued: DirectPathTicketV1
        private var remoteSequence = 0L
        var nativeV4Available = false
        var relayMutations = 0
        val offerGatheringStates = mutableListOf<Boolean>()
        val candidateUpdates = mutableListOf<Boolean>()
        var nominatedRank = 0
        private val orderedPairs = mutableListOf<Pair<List<Byte>, List<Byte>>>()
        private var checkBatchCount = 0
        private var remoteTrickleSent = false

        override val incoming: Flow<TransportSignalEnvelopeV1> = channel.receiveAsFlow()
        override val state: StateFlow<DirectTransportSignalingState> = mutableState
        override val multipairV2Active: Boolean
            get() = nativeV4Available && multipairCapability

        override suspend fun fetchConfig(): DirectSignalingCallResult<DirectTransportServerConfig> =
            DirectSignalingCallResult.Success(config(multipairCapability))

        override fun start(): Boolean = true

        override suspend fun issueTicket(
            targetDeviceId: UUID,
            connectionId: UUID,
            config: DirectTransportServerConfig,
        ): DirectSignalingCallResult<ValidatedDirectPathTicket> {
            connection = connectionId
            issued = ticket(connectionId)
            return DirectSignalingCallResult.Success(ValidatedDirectPathTicket(issued, issued.toByteArray()))
        }

        override fun send(envelope: TransportSignalEnvelopeV1): Boolean {
            assertEquals(ByteString.copyFromUtf8(pcKeyId), envelope.keyId)
            assertFalse(envelope.keyId == ByteString.copyFromUtf8(androidKeyId))
            val plaintext = DirectTransportCrypto.decryptSignal(key, envelope)
            try {
                val payload = TransportSignalPayloadV1.parseFrom(plaintext)
                when (envelope.signalType) {
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_CLOSE -> {
                        assertEquals("DIRECT_ATTEMPT_RETIRED", payload.pathClose.reasonCode)
                        closeSignals++
                    }
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_OFFER -> {
                        offerGatheringStates += payload.pathOffer.gatheringComplete
                        emitAnswer(envelope)
                    }
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CANDIDATE_UPDATE ->
                        candidateUpdates += payload.candidateUpdate.gatheringComplete
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2 -> {
                        checkBatchCount++
                        payload.connectivityCheckBatchV2.pairsList.forEach { pair ->
                            orderedPairs += pair.initiatorCandidateId.toByteArray().toList() to
                                pair.responderCandidateId.toByteArray().toList()
                        }
                        if (!remoteTrickleSent && remoteTrickleAfterCheckBatch == checkBatchCount) {
                            remoteTrickleSent = true
                            emitRemoteCandidateUpdate(envelope)
                        }
                    }
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2 -> {
                        val nomination = payload.nominationV2
                        nominatedRank = orderedPairs.indexOf(
                            nomination.winningPair.initiatorCandidateId.toByteArray().toList() to
                                nomination.winningPair.responderCandidateId.toByteArray().toList(),
                        ) + 1
                        val selected = if (mismatchAck) {
                            nomination.winningPair.toBuilder()
                                .setResponderCandidateId(ByteString.copyFrom(ByteArray(16) { 0x7e }))
                                .build()
                        } else nomination.winningPair
                        val ack = DirectNominationAckV2.newBuilder()
                            .setNominationId(nomination.nominationId)
                            .setCheckRoundId(nomination.checkRoundId)
                            .setWinningPair(selected)
                            .setResult(DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED)
                            .build()
                        emit(
                            envelope,
                            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
                            TransportSignalPayloadV1.newBuilder().setFormatVersion(1)
                                .setNominationAckV2(ack).build(),
                        )
                    }
                    TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2 -> {
                        val local = payload.nominationResultV2
                        val result = DirectNominationResultV2.newBuilder(local).build()
                        emit(
                            envelope,
                            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
                            TransportSignalPayloadV1.newBuilder().setFormatVersion(1)
                                .setNominationResultV2(result).build(),
                        )
                    }
                    else -> return false
                }
                return true
            } finally {
                plaintext.fill(0)
            }
        }

        private fun emitAnswer(offer: TransportSignalEnvelopeV1) {
            val answer = DirectPathAnswerV1.newBuilder()
                .setTicketId(issued.ticketId)
                .setQuicCertificateSha256(ByteString.copyFrom(ByteArray(32) { 0x66 }))
                .addAllCandidates(answerCandidates)
                .setGatheringComplete(answerGatheringComplete)
                .setResult(DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED)
                .build()
            emit(
                offer,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_ANSWER,
                TransportSignalPayloadV1.newBuilder().setFormatVersion(1).setPathAnswer(answer).build(),
            )
        }

        private fun emitRemoteCandidateUpdate(outbound: TransportSignalEnvelopeV1) {
            val update = dev.termlink.protocol.transport.v1.DirectCandidateUpdateV1.newBuilder()
                .addAllCandidates(remoteTrickleCandidates)
                .setGatheringComplete(true)
                .build()
            emit(
                outbound,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CANDIDATE_UPDATE,
                TransportSignalPayloadV1.newBuilder().setFormatVersion(1)
                    .setCandidateUpdate(update).build(),
            )
        }

        private fun emit(
            outbound: TransportSignalEnvelopeV1,
            type: TransportSignalTypeV1,
            payload: TransportSignalPayloadV1,
        ) {
            assertEquals(ByteString.copyFromUtf8(pcKeyId), outbound.keyId)
            assertFalse(pcKeyId == androidKeyId)
            val metadata = TransportSignalEnvelopeV1.newBuilder()
                .setProtocolVersion(1)
                .setSignalType(type)
                .setConnectionId(ByteString.copyFrom(connection.bytes()))
                .setSenderDeviceId(ByteString.copyFrom(remoteId.bytes()))
                .setRecipientDeviceId(ByteString.copyFrom(localId.bytes()))
                .setPathEpoch(issued.pathEpoch)
                .setSignalSeq(++remoteSequence)
                .setSignalAck(outbound.signalSeq)
                .setSentAtMs(clock)
                // The pairwise envelope key belongs to the PC peer. Do not copy a field from the
                // Android outbound envelope here: doing so would hide a cross-platform key-id bug.
                .setKeyId(ByteString.copyFromUtf8(pcKeyId))
                .build()
            assertEquals(ByteString.copyFromUtf8(pcKeyId), metadata.keyId)
            val bytes = payload.toByteArray()
            try {
                assertTrue(channel.trySend(DirectTransportCrypto.encryptSignal(key, metadata, bytes)).isSuccess)
            } finally {
                bytes.fill(0)
            }
        }

        var closeSignals = 0
        override fun close() = Unit
    }

    private inner class MultipairNative(
        private val clock: Long,
        private val delayedGatherCompletion: Boolean = false,
        private val localReflexiveCount: Int = 0,
        private val validatedRank: Int = 1,
        private val generationStep: Long = 1,
    ) : ProductionDirectShadowNativeV4 {
        private val events = ArrayDeque<DirectNativeEventV4>()
        private var generation = 0L
        private lateinit var gather: DirectNativeGatherRequestV4
        private var begin: DirectNativeBeginChecksRequestV4? = null
        private var gatherCompletionEmitted = false
        var beginBeforeGatherComplete = false
        var gatherCalls = 0
        var closeCalls = 0
        val submittedPairs = mutableListOf<DirectNativeCandidatePairV4>()
        var committedPair: DirectNativeCandidatePairV4? = null

        override fun gather(request: DirectNativeGatherRequestV4): DirectNativeResultV4<Unit> {
            assertEquals(clock + 60_000L, request.candidateExpiresAtMillis)
            gatherCalls++
            gather = request
            events += opaqueEvent(DirectNativeEventKindV4.IDENTITY_READY, ByteArray(96) { 0x22 })
            events += candidateBatch(1, listOf(localHost()), total = 1, complete = false)
            if (!delayedGatherCompletion) enqueueGatherCompletion()
            return DirectNativeResultV4.Success(Unit)
        }

        override fun beginChecks(request: DirectNativeBeginChecksRequestV4): DirectNativeResultV4<Unit> {
            begin = request
            beginBeforeGatherComplete = !gatherCompletionEmitted
            if (delayedGatherCompletion && !gatherCompletionEmitted) enqueueGatherCompletion()
            submittedPairs += request.pairs
            events += pairValidated(request.pairs.single { it.rank == validatedRank })
            if (request.planComplete) events += checksComplete()
            return DirectNativeResultV4.Success(Unit)
        }

        override fun appendCheckPairs(
            request: DirectNativeAppendCheckPairsRequestV4,
        ): DirectNativeResultV4<Unit> {
            submittedPairs += request.pairs
            if (request.planComplete) events += checksComplete()
            return DirectNativeResultV4.Success(Unit)
        }

        override fun commitNomination(
            request: DirectNativeCommitNominationRequestV4,
        ): DirectNativeResultV4<Unit> {
            committedPair = request.winningPair
            events += nominationReady(request)
            events += opaqueEvent(DirectNativeEventKindV4.SIGN_REQUEST, ByteArray(80) { 0x33 }, 19)
            events += shadowReady(request, ByteArray(32) { 0x7a })
            return DirectNativeResultV4.Success(Unit)
        }

        override fun cancel(epoch: Long, attemptId: ByteArray) = DirectNativeResultV4.Success(Unit)
        override fun networkChanged(epoch: Long, attemptId: ByteArray) = DirectNativeResultV4.Success(Unit)
        override fun submitSignature(
            epoch: Long,
            attemptId: ByteArray,
            requestId: Long,
            signature: ByteArray,
        ) = DirectNativeResultV4.Success(Unit)

        override fun poll(): DirectNativeResultV4<DirectNativeEventV4?> {
            return DirectNativeResultV4.Success(events.removeFirstOrNull())
        }

        override fun close() { closeCalls++ }

        private fun enqueueGatherCompletion() {
            val additions = (0 until localReflexiveCount).map { index -> localReflexive(index) }
            val event = candidateBatch(2, additions, 1 + additions.size, complete = true)
            events += event
            gatherCompletionEmitted = true
        }

        private fun localHost() = nativeCandidate(
            ByteArray(16) { 0x41 }, ByteArray(16), "192.168.1.10", 50_010, true, 2_000_000_000,
        )

        private fun localReflexive(index: Int) = nativeCandidate(
            ByteArray(16) { (0x42 + index).toByte() }, ByteArray(16) { 0x41 },
            "203.0.113.${10 + index}", 51_010 + index, false, 1_900_000_000 - index,
        )

        private fun nativeCandidate(
            id: ByteArray,
            related: ByteArray,
            address: String,
            port: Int,
            host: Boolean,
            priority: Int,
        ) = DirectNativeCandidateV4(
            id, related, DirectNativeSocketAddressV4(InetAddress.getByName(address).address, port),
            if (host) DirectNativeCandidateKindV4.HOST else DirectNativeCandidateKindV4.SERVER_REFLEXIVE,
            priority, 0, clock + 120_000,
        )

        private fun baseEvent(
            kind: DirectNativeEventKindV4,
            state: DirectNativePathStateV4,
            payload: ByteArray,
            requestId: Long = 0,
        ): DirectNativeEventV4 {
            generation += generationStep
            return DirectNativeEventV4(
                kind, state, 0, gather.epoch, generation, requestId, gather.attemptId.copyOf(),
                null, payload,
            )
        }

        private fun opaqueEvent(kind: DirectNativeEventKindV4, data: ByteArray, requestId: Long = 0):
            DirectNativeEventV4 {
            val bytes = ByteArray(2_080)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(4).putInt(2_080).putInt(data.size).putInt(0).put(data)
            return baseEvent(
                kind,
                if (kind == DirectNativeEventKindV4.SIGN_REQUEST) {
                    DirectNativePathStateV4.AUTHENTICATING
                } else DirectNativePathStateV4.GATHERING,
                bytes,
                requestId,
            )
        }

        private fun candidateBatch(
            sequence: Int,
            candidates: List<DirectNativeCandidateV4>,
            total: Int,
            complete: Boolean,
        ): DirectNativeEventV4 {
            val bytes = ByteArray(1_088)
            val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            buffer.putInt(4).putInt(1_088).putLong(gather.epoch).put(gather.attemptId)
                .putInt(sequence).putInt(candidates.size).putInt(total).putInt(if (complete) 1 else 0)
            candidates.forEach { buffer.put(encodeCandidateAbi(it)) }
            return baseEvent(
                DirectNativeEventKindV4.CANDIDATE_BATCH,
                DirectNativePathStateV4.GATHERING,
                bytes,
            )
        }

        private fun pairValidated(pair: DirectNativeCandidatePairV4): DirectNativeEventV4 {
            val bytes = ByteArray(360)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(4).putInt(360).putLong(gather.epoch).put(gather.attemptId)
                .put(checkNotNull(begin).checkRoundId).put(encodePairAbi(pair)).putInt(12).putInt(2)
            return baseEvent(
                DirectNativeEventKindV4.PAIR_VALIDATED,
                DirectNativePathStateV4.CHECKING,
                bytes,
            )
        }

        private fun checksComplete(): DirectNativeEventV4 {
            val bytes = ByteArray(80)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(4).putInt(80).putLong(gather.epoch).put(gather.attemptId)
                .put(checkNotNull(begin).checkRoundId).putInt(submittedPairs.size)
                .putInt(1).putInt(submittedPairs.size - 1).putInt(1)
            return baseEvent(
                DirectNativeEventKindV4.CHECKS_COMPLETE,
                DirectNativePathStateV4.NOMINATION_PENDING,
                bytes,
            )
        }

        private fun nominationReady(request: DirectNativeCommitNominationRequestV4): DirectNativeEventV4 {
            val bytes = ByteArray(376)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(4).putInt(376).putLong(gather.epoch).put(gather.attemptId)
                .put(request.checkRoundId).put(request.nominationId).put(encodePairAbi(request.winningPair))
                .putInt(request.role.code).putInt(0)
            return baseEvent(
                DirectNativeEventKindV4.NOMINATION_READY,
                DirectNativePathStateV4.NOMINATION_PENDING,
                bytes,
            )
        }

        private fun shadowReady(
            request: DirectNativeCommitNominationRequestV4,
            binding: ByteArray,
        ): DirectNativeEventV4 {
            val bytes = ByteArray(400)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(4).putInt(400).putLong(gather.epoch).put(gather.attemptId)
                .put(request.checkRoundId).put(request.nominationId).put(encodePairAbi(request.winningPair))
                .put(binding)
            return baseEvent(
                DirectNativeEventKindV4.SHADOW_READY,
                DirectNativePathStateV4.SHADOW_DIRECT,
                bytes,
            )
        }

        private fun encodePairAbi(pair: DirectNativeCandidatePairV4): ByteArray {
            val bytes = ByteArray(288)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                .putInt(4).putInt(288).putInt(pair.rank).putInt(0)
                .put(encodeCandidateAbi(pair.initiator))
                .put(encodeCandidateAbi(pair.responder))
            return bytes
        }

        private fun encodeCandidateAbi(candidate: DirectNativeCandidateV4): ByteArray {
            val bytes = ByteArray(128)
            val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            buffer.putInt(4).putInt(128).put(candidate.candidateId).put(candidate.relatedCandidateId)
            buffer.putInt(4).putInt(48)
            buffer.put(candidate.endpoint.ipAddress.copyOf(16))
            buffer.putInt(candidate.endpoint.ipAddress.size).putInt(candidate.endpoint.port)
            buffer.putLong(0).putLong(0)
            buffer.putInt(candidate.kind.code).putInt(1).putInt(candidate.priority)
                .putInt(candidate.networkCost).putLong(candidate.expiresAtMillis)
            return bytes
        }
    }

    private class RejectingV3Native : ProductionDirectShadowNative {
        var gatherCalls = 0
        override fun gather(request: DirectNativeGatherRequest): DirectNativeResult<Unit> {
            gatherCalls++
            return DirectNativeResult.Failure(DirectNativeEvent.relayOnly(1_003))
        }
        override fun markCandidatesExchanged(
            epoch: Long,
            attemptId: ByteArray,
            generation: Long,
            candidates: List<DirectNativeSocketAddress>,
        ) = DirectNativeResult.Success(Unit)
        override fun nominate(command: ProductionDirectNominationCommand) = DirectNativeResult.Success(Unit)
        override fun cancel(epoch: Long, attemptId: ByteArray) = DirectNativeResult.Success(Unit)
        override fun networkChanged(epoch: Long, attemptId: ByteArray) = DirectNativeResult.Success(Unit)
        override fun submitSignature(
            epoch: Long,
            attemptId: ByteArray,
            requestId: Long,
            signature: ByteArray,
        ) = DirectNativeResult.Success(Unit)
        override fun poll() = DirectNativeResult.Success<DirectNativeEvent?>(null)
        override fun close() = Unit
    }

    private class FakePlatform : ProductionDirectShadowPlatform {
        override fun capture(network: android.net.Network): CapturedDirectNetwork? = null
        override suspend fun prepare(
            network: CapturedDirectNetwork,
            stunServers: List<String>,
        ) = PreparedDirectGather(
            network.handle,
            DirectNativeSocketAddress(ByteArray(4), 0),
            network.advertisedAddress.address,
            listOf(DirectNativeSocketAddress(byteArrayOf(1, 1, 1, 1), 3478)),
        )
    }

    private class DeterministicUuids : () -> UUID {
        private val next = AtomicInteger(1)
        override fun invoke(): UUID = UUID.fromString(
            if (next.getAndIncrement() == 1) "30313233-3435-4637-b839-3a3b3c3d3e3f"
            else "40414243-4445-4647-a849-4a4b4c4d4e4f",
        )
    }

    private class FixedRandom : SecureRandom() {
        private var seed = 1
        override fun nextBytes(bytes: ByteArray) {
            bytes.indices.forEach { bytes[it] = (seed + it).toByte() }
            seed++
        }
    }

    private fun UUID.bytes(): ByteArray = ByteBuffer.allocate(16)
        .putLong(mostSignificantBits).putLong(leastSignificantBits).array()

    private companion object { const val AUTH_GENERATION = 8L }
}

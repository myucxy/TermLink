package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.crypto.DirectTransportCrypto
import dev.termlink.network.direct.nativebridge.DirectNativeCandidateKindV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidatePairV4
import dev.termlink.network.direct.nativebridge.DirectNativeCandidateV4
import dev.termlink.network.direct.nativebridge.DirectNativeCarrierV5
import dev.termlink.network.direct.nativebridge.DirectNativeCommitNominationRequestV4
import dev.termlink.network.direct.nativebridge.DirectNativeCursorV5
import dev.termlink.network.direct.nativebridge.DirectNativeDirectionV5
import dev.termlink.network.direct.nativebridge.DirectNativeEventV5
import dev.termlink.network.direct.nativebridge.DirectNativeIngressV5
import dev.termlink.network.direct.nativebridge.DirectNativePathInfoV5
import dev.termlink.network.direct.nativebridge.DirectNativePathKindV5
import dev.termlink.network.direct.nativebridge.DirectNativePathV5
import dev.termlink.network.direct.nativebridge.DirectNativeProposalV5
import dev.termlink.network.direct.nativebridge.DirectNativeResultV5
import dev.termlink.network.direct.nativebridge.DirectNativeRoleV4
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionKindV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteActionV5
import dev.termlink.network.direct.nativebridge.DirectNativeRouteEnvelopeV5
import dev.termlink.network.direct.nativebridge.DirectNativeSigningPayloadV5
import dev.termlink.network.direct.nativebridge.DirectNativeSocketAddressV4
import dev.termlink.network.direct.nativebridge.DirectNativeTransitionV5
import dev.termlink.network.production.ProductionRealtimeMultiplexer
import dev.termlink.network.production.ProductionRealtimeNegotiatedCapabilities
import dev.termlink.network.production.ProductionRealtimeSocket
import dev.termlink.network.production.ProductionRealtimeSocketDiagnostics
import dev.termlink.network.production.ProductionReceivePressure
import dev.termlink.network.production.ProductionReceivedEnvelope
import dev.termlink.network.production.RealtimeState
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.transport.v1.DirectCutoverAcceptV1
import dev.termlink.protocol.transport.v1.DirectCutoverCommitV1
import dev.termlink.protocol.transport.v1.DirectFallbackV1
import dev.termlink.protocol.transport.v1.DirectPathCloseV1
import dev.termlink.protocol.transport.v1.DirectPathResultV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalPayloadV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout

/**
 * Integration boundary for the Android v4-to-v5 cutover coordinator.
 *
 * Termux is used only as a lifecycle-behaviour reference here: its pinned terminal core keeps
 * bounded queues and explicit close ownership, but it has no Relay/NAT/E2EE cutover layer that
 * can be reused. These tests therefore keep TermLink's authenticated signalling, actor fences,
 * and peer-local failure ownership authoritative.
 */
class ProductionDirectDataCoordinatorTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val closeables = ArrayList<AutoCloseable>()

    @AfterTest
    fun tearDown() {
        closeables.asReversed().forEach { runCatching(it::close) }
        scope.cancel()
    }

    @Test
    fun attachRuntimeOwnsExactlyOneSignallingCollector(): Unit = runBlocking {
        val fixture = runtimeFixture(attach = false)

        assertTrue(fixture.coordinator.attachRuntime(fixture.runtime))
        withTimeout(2_000) {
            while (fixture.lane.collectionStarts.get() != 1) delay(1)
        }
        assertFalse(fixture.coordinator.attachRuntime(fixture.runtime))
        delay(50)

        assertEquals(1, fixture.lane.collectionStarts.get())
    }

    @Test
    fun v4HandoffIsConsumedOnceAndAlwaysClosesItsSource(): Unit = runBlocking {
        val fixture = runtimeFixture()

        val successNative = FakeNative(path(epoch = 7, connectionSeed = 20, attemptSeed = 21))
        val successSource = FakeV4Source { DirectNativeResultV5.Success(successNative) }
        val success = handoff(fixture, uuid(2), successNative.path, successSource)
        assertTrue(fixture.coordinator.promoteV4AndInstall(success))
        assertFalse(fixture.coordinator.promoteV4AndInstall(success))
        assertEquals(1, successSource.promoteCalls.get())
        assertEquals(1, successSource.closes.get())
        withTimeout(2_000) {
            while (uuid(2) !in fixture.runtime.carriers.value) delay(1)
        }

        val rejectedPath = path(epoch = 8, connectionSeed = 22, attemptSeed = 23)
        val rejectedSource = FakeV4Source {
            DirectNativeResultV5.Failure(2_003, pathEpoch = rejectedPath.epoch)
        }
        val rejected = handoff(fixture, uuid(3), rejectedPath, rejectedSource)
        assertFalse(fixture.coordinator.promoteV4AndInstall(rejected))
        assertFalse(fixture.coordinator.promoteV4AndInstall(rejected))
        assertEquals(1, rejectedSource.promoteCalls.get())
        assertEquals(1, rejectedSource.closes.get())
        assertFalse(uuid(3) in fixture.runtime.carriers.value)

        val throwingPath = path(epoch = 9, connectionSeed = 24, attemptSeed = 25)
        val throwingSource = FakeV4Source { throw IllegalStateException("synthetic promotion failure") }
        val throwing = handoff(fixture, uuid(4), throwingPath, throwingSource)
        assertFalse(fixture.coordinator.promoteV4AndInstall(throwing))
        assertFalse(fixture.coordinator.promoteV4AndInstall(throwing))
        assertEquals(1, throwingSource.promoteCalls.get())
        assertEquals(1, throwingSource.closes.get())
        assertFalse(uuid(4) in fixture.runtime.carriers.value)
    }

    @Test
    fun signalTypesSixThroughTenDispatchOnlyToTheirExactNativeOperation() {
        val proposalFixture = directFixture()
        val inboundProposal = proposal(proposalFixture, Direction.PC_TO_ANDROID, barrierSeed = 30)
        assertTrue(
            proposalFixture.coordinator.process(
                proposalFixture.actor,
                proposalFixture.seal(
                    TYPE_PROPOSAL,
                    proposalPayload(inboundProposal),
                    sequence = 1,
                ),
            ),
        )
        assertEquals(1, proposalFixture.native.receivedProposals.size)

        val acceptFixture = directFixture()
        val outboundProposal = proposal(acceptFixture, Direction.ANDROID_TO_PC, barrierSeed = 31)
        assertTrue(acceptFixture.coordinator.startCutover(acceptFixture.actor, outboundProposal))
        assertTrue(
            acceptFixture.coordinator.process(
                acceptFixture.actor,
                acceptFixture.seal(
                    TYPE_ACCEPT,
                    acceptPayload(outboundProposal),
                    sequence = 1,
                    acknowledgement = 1,
                ),
            ),
        )
        assertEquals(1, acceptFixture.native.receivedAccepts.size)

        val commitFixture = directFixture()
        val commitProposal = proposal(commitFixture, Direction.PC_TO_ANDROID, barrierSeed = 32)
        assertTrue(commitFixture.coordinator.process(commitFixture.actor, commitFixture.seal(
            TYPE_PROPOSAL, proposalPayload(commitProposal), sequence = 1,
        )))
        assertTrue(commitFixture.coordinator.process(commitFixture.actor, commitFixture.seal(
            TYPE_COMMIT, commitPayload(commitProposal), sequence = 2,
        )))
        assertEquals(1, commitFixture.native.receivedCommits.size)

        val fallbackFixture = directFixture()
        val fallbackProposal = proposal(fallbackFixture, Direction.PC_TO_ANDROID, barrierSeed = 33)
        assertTrue(fallbackFixture.coordinator.process(fallbackFixture.actor, fallbackFixture.seal(
            TYPE_PROPOSAL, proposalPayload(fallbackProposal), sequence = 1,
        )))
        assertTrue(fallbackFixture.coordinator.process(fallbackFixture.actor, fallbackFixture.seal(
            TYPE_FALLBACK, fallbackPayload(fallbackProposal, 4), sequence = 2,
        )))
        assertEquals(1, fallbackFixture.native.receivedFallbacks.size)

        val closeFixture = directFixture()
        assertFalse(closeFixture.coordinator.process(closeFixture.actor, closeFixture.seal(
            TYPE_CLOSE, closePayload(), sequence = 1,
        )))
        assertEquals(0, closeFixture.native.totalCutoverCalls())
    }

    @Test
    fun proposalMustBeCanonicalEncryptedAndMatchItsAuthenticatedOuterType() {
        val accepted = directFixture()
        val canonical = proposal(accepted, Direction.PC_TO_ANDROID, barrierSeed = 40)
        val canonicalBytes = proposalPayload(canonical).toByteArray()
        assertTrue(accepted.coordinator.process(
            accepted.actor,
            accepted.sealRaw(TYPE_PROPOSAL, canonicalBytes, sequence = 1),
        ))
        assertEquals(Direction.PC_TO_ANDROID, accepted.native.receivedProposals.single().direction.toRealtime())

        val unknownField = directFixture()
        val unknownProposal = proposal(unknownField, Direction.PC_TO_ANDROID, barrierSeed = 41)
        val nonCanonical = proposalPayload(unknownProposal).toByteArray() +
            byteArrayOf(0xf8.toByte(), 0x07, 0x01)
        assertFalse(unknownField.coordinator.process(
            unknownField.actor,
            unknownField.sealRaw(TYPE_PROPOSAL, nonCanonical, sequence = 1),
        ))
        assertTrue(unknownField.native.receivedProposals.isEmpty())

        val mismatched = directFixture()
        val mismatchedProposal = proposal(mismatched, Direction.PC_TO_ANDROID, barrierSeed = 42)
        assertFalse(mismatched.coordinator.process(
            mismatched.actor,
            mismatched.seal(TYPE_ACCEPT, proposalPayload(mismatchedProposal), sequence = 1),
        ))
        assertEquals(0, mismatched.native.totalCutoverCalls())

        val tampered = directFixture()
        val tamperedProposal = proposal(tampered, Direction.PC_TO_ANDROID, barrierSeed = 43)
        val sealed = tampered.seal(TYPE_PROPOSAL, proposalPayload(tamperedProposal), sequence = 1)
        val ciphertext = sealed.ciphertext.toByteArray().also {
            it[it.lastIndex] = (it.last().toInt() xor 1).toByte()
        }
        val modified = sealed.toBuilder().setCiphertext(ByteString.copyFrom(ciphertext)).build()
        ciphertext.fill(0)
        assertFalse(tampered.coordinator.process(tampered.actor, modified))
        assertTrue(tampered.native.receivedProposals.isEmpty())
    }

    @Test
    fun sequenceAcknowledgementKeyGenerationAndEveryPathFenceAreStrict() {
        val valid = directFixture(localSequence = 2, remoteSequence = 3, remoteAcknowledgement = 1)
        val validProposal = proposal(valid, Direction.PC_TO_ANDROID, barrierSeed = 50)
        assertTrue(valid.coordinator.process(valid.actor, valid.seal(
            TYPE_PROPOSAL, proposalPayload(validProposal), sequence = 4, acknowledgement = 1,
        )))

        val mutations = listOf<Pair<String, (TransportSignalEnvelopeV1.Builder) -> Unit>>(
            "sequence gap" to { it.signalSeq = 5 },
            "ack regression" to { it.signalAck = 0 },
            "ack beyond local" to { it.signalAck = 3 },
            "key id" to { it.keyId = ByteString.copyFrom(byteArrayOf(99)) },
            "connection" to { it.connectionId = ByteString.copyFrom(uuidBytes(uuid(99))) },
            "path epoch" to { it.pathEpoch = 8 },
            "sender peer" to { it.senderDeviceId = ByteString.copyFrom(uuidBytes(uuid(98))) },
            "recipient peer" to { it.recipientDeviceId = ByteString.copyFrom(uuidBytes(uuid(97))) },
        )
        for ((name, mutate) in mutations) {
            val fixture = directFixture(localSequence = 2, remoteSequence = 3, remoteAcknowledgement = 1)
            val value = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 51)
            assertFalse(
                fixture.coordinator.process(
                    fixture.actor,
                    fixture.seal(
                        TYPE_PROPOSAL,
                        proposalPayload(value),
                        sequence = 4,
                        acknowledgement = 1,
                        mutate = mutate,
                    ),
                ),
                name,
            )
            assertTrue(fixture.native.receivedProposals.isEmpty(), name)
        }

        val generation = directFixture()
        generation.lane.negotiated.value = capabilities(generation = SIGNAL_GENERATION + 1)
        val generationProposal = proposal(generation, Direction.PC_TO_ANDROID, barrierSeed = 52)
        assertTrue(generation.coordinator.process(generation.actor, generation.seal(
            TYPE_PROPOSAL, proposalPayload(generationProposal), sequence = 1,
        )))

        val actorFence = directFixture()
        val actorProposal = proposal(actorFence, Direction.PC_TO_ANDROID, barrierSeed = 53)
        val validEnvelope = actorFence.seal(TYPE_PROPOSAL, proposalPayload(actorProposal), sequence = 1)
        assertFalse(actorFence.coordinator.process(
            actorFence.actor.copy(keyEpoch = KEY_EPOCH + 1),
            validEnvelope,
        ))
        assertFalse(actorFence.coordinator.process(
            actorFence.actor.copy(peerDeviceId = uuid(96)),
            validEnvelope,
        ))
        assertFalse(actorFence.coordinator.process(
            actorFence.actor.copy(
                pathFence = ProductionDirectPathFence(
                    PATH_EPOCH + 1,
                    actorFence.fence.connectionId,
                    uuid(95),
                ),
            ),
            validEnvelope,
        ))
    }

    @Test
    fun outboundTypesSixThroughNineAreCanonicalEncryptedAndFenced() {
        val fixture = directFixture()
        val value = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 60)
        assertTrue(fixture.coordinator.startCutover(fixture.actor, value))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.ACCEPT, value, accepted = true),
        ))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.COMMIT, value),
        ))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.FALLBACK, value, reason = 1),
        ))

        assertEquals(listOf(TYPE_PROPOSAL, TYPE_ACCEPT, TYPE_COMMIT, TYPE_FALLBACK),
            fixture.lane.sent.map { it.signalType })
        fixture.lane.sent.forEachIndexed { index, envelope ->
            assertEquals((index + 1).toLong(), envelope.signalSeq)
            assertEquals(0, envelope.signalAck)
            assertEquals(PATH_EPOCH, envelope.pathEpoch)
            assertContentEquals(uuidBytes(fixture.fence.connectionId), envelope.connectionId.toByteArray())
            assertContentEquals(KEY_ID, envelope.keyId.toByteArray())
            assertContentEquals(uuidBytes(LOCAL), envelope.senderDeviceId.toByteArray())
            assertContentEquals(uuidBytes(PEER), envelope.recipientDeviceId.toByteArray())
            val plaintext = DirectTransportCrypto.decryptSignal(PAIRWISE_KEY, envelope)
            try {
                val parsed = TransportSignalPayloadV1.parseFrom(plaintext)
                assertContentEquals(parsed.toByteArray(), plaintext)
                assertEquals(1, parsed.formatVersion)
                assertEquals(envelope.signalType, parsed.type())
            } finally {
                plaintext.fill(0)
            }
        }
    }

    @Test
    fun fallbackIsRejectedBeforeSharedSignallingWhenGenerationOrCapabilityIsStale() {
        val current = directFixture()
        val currentValue = proposal(current, Direction.PC_TO_ANDROID, barrierSeed = 61)
        assertTrue(current.coordinator.process(current.actor, current.seal(
            TYPE_PROPOSAL, proposalPayload(currentValue), sequence = 1,
        )))

        // A current generation with the negotiated data capability still
        // permits the native fallback action to produce one encrypted frame.
        assertTrue(current.coordinator.processNativeAction(
            current.actor,
            action(DirectNativeRouteActionKindV5.FALLBACK, currentValue, reason = 1),
        ))
        assertEquals(1, current.lane.sendCalls.get())
        assertEquals(1, current.lane.sent.size)

        // A stale signalling generation must fail closed locally.  In
        // particular, no call reaches the shared client and it remains alive.
        val staleGeneration = directFixture()
        val staleGenerationValue = proposal(staleGeneration, Direction.PC_TO_ANDROID, barrierSeed = 62)
        assertTrue(staleGeneration.coordinator.process(staleGeneration.actor, staleGeneration.seal(
            TYPE_PROPOSAL, proposalPayload(staleGenerationValue), sequence = 1,
        )))
        staleGeneration.lane.negotiated.value = capabilities(generation = SIGNAL_GENERATION + 1)
        assertFalse(staleGeneration.coordinator.processNativeAction(
            staleGeneration.actor,
            action(DirectNativeRouteActionKindV5.FALLBACK, staleGenerationValue, reason = 1),
        ))
        assertEquals(0, staleGeneration.lane.sendCalls.get())
        assertTrue(staleGeneration.lane.sent.isEmpty())
        assertFalse(staleGeneration.lane.closed.get())

        // Capability withdrawal is the same fail-closed boundary even when
        // the generation itself is unchanged.
        val staleCapability = directFixture()
        val staleCapabilityValue = proposal(staleCapability, Direction.PC_TO_ANDROID, barrierSeed = 63)
        assertTrue(staleCapability.coordinator.process(staleCapability.actor, staleCapability.seal(
            TYPE_PROPOSAL, proposalPayload(staleCapabilityValue), sequence = 1,
        )))
        staleCapability.lane.negotiated.value = DirectTransportSignalingNegotiatedCapabilities(
            SIGNAL_GENERATION,
            setOf(DATA_CAPABILITY),
        )
        assertFalse(staleCapability.coordinator.processNativeAction(
            staleCapability.actor,
            action(DirectNativeRouteActionKindV5.FALLBACK, staleCapabilityValue, reason = 1),
        ))
        assertEquals(0, staleCapability.lane.sendCalls.get())
        assertTrue(staleCapability.lane.sent.isEmpty())
        assertFalse(staleCapability.lane.closed.get())
    }

    @Test
    fun committedFallbackAppliesLocallyWhileSignallingIsUnavailableThenConvergesOnReconnect() {
        val observations = CopyOnWriteArrayList<CutoverObservation>()
        val fixture = directFixture(observations)
        val committed = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 68)
        commitOutbound(fixture, committed, remoteSequence = 0)
        val sendsBeforeFallback = fixture.lane.sent.size

        val unavailable = DirectTransportSignalingNegotiatedCapabilities(
            SIGNAL_GENERATION + 1,
            emptySet(),
        )
        fixture.lane.negotiated.value = unavailable
        assertTrue(fixture.coordinator.signalingCapabilitiesChanged(fixture.actor, unavailable))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.FALLBACK, committed, reason = 1),
        ))

        assertEquals(sendsBeforeFallback, fixture.lane.sent.size)
        assertEquals(0, fixture.lane.sendCalls.get() - sendsBeforeFallback)
        assertTrue(observations.last().activeSessionIds.isEmpty())
        assertFalse(observations.last().committed)
        assertFalse(fixture.lane.closed.get())

        val restored = capabilities(generation = SIGNAL_GENERATION + 2)
        fixture.lane.negotiated.value = restored
        assertTrue(fixture.coordinator.signalingCapabilitiesChanged(fixture.actor, restored))

        assertEquals(sendsBeforeFallback + 1, fixture.lane.sent.size)
        assertEquals(TYPE_FALLBACK, fixture.lane.sent.last().signalType)
        assertEquals((sendsBeforeFallback + 1).toLong(), fixture.lane.sent.last().signalSeq)
        assertFalse(fixture.lane.closed.get())

        val lateSession = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 69)
        assertTrue(fixture.coordinator.startCutover(fixture.actor, lateSession))
        assertEquals(TYPE_PROPOSAL, fixture.lane.sent.last().signalType)
        assertEquals((sendsBeforeFallback + 2).toLong(), fixture.lane.sent.last().signalSeq)
    }

    @Test
    fun generationChangeClearsOnlyNegotiatingBarrierAndPreservesCommittedBatch() {
        val observations = CopyOnWriteArrayList<CutoverObservation>()
        val fixture = directFixture(observations)
        val committed = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 66)
        val negotiating = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 67)
        commitOutbound(fixture, committed, remoteSequence = 0)
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL,
            proposalPayload(negotiating),
            sequence = 3,
            acknowledgement = 1,
        )))

        val changed = DirectTransportSignalingNegotiatedCapabilities(
            SIGNAL_GENERATION + 1,
            emptySet(),
        )
        fixture.lane.negotiated.value = changed
        assertTrue(fixture.coordinator.signalingCapabilitiesChanged(fixture.actor, changed))

        val state = assertNotNull(field<MutableMap<UUID, Any>>(fixture.coordinator, "peers")[PEER])
        assertTrue(field<MutableMap<*, *>>(state, "barriers").isEmpty())
        assertEquals(1, field<MutableList<*>>(state, "committedBarriers").size)
        assertEquals(1, fixture.native.receivedFallbacks.size)
        assertEquals(DirectNativeDirectionV5.PC_TO_ANDROID, observations.last().direction)
        assertFalse(observations.last().committed)
        assertTrue(observations.last().activeSessionIds.isEmpty())
        assertFalse(fixture.lane.closed.get())
    }

    @Test
    fun inboundFallbackRebindsToNewAdmittedGenerationButRejectsMissingCapabilities() {
        val generation = directFixture()
        val generationProposal = proposal(generation, Direction.PC_TO_ANDROID, barrierSeed = 62)
        assertTrue(generation.coordinator.process(generation.actor, generation.seal(
            TYPE_PROPOSAL, proposalPayload(generationProposal), sequence = 1,
        )))
        generation.lane.negotiated.value = capabilities(generation = SIGNAL_GENERATION + 1)
        assertTrue(generation.coordinator.process(generation.actor, generation.seal(
            TYPE_FALLBACK, fallbackPayload(generationProposal, 4), sequence = 2,
        )))
        assertEquals(1, generation.native.receivedFallbacks.size)
        assertFalse(generation.lane.closed.get())

        val capability = directFixture()
        val capabilityProposal = proposal(capability, Direction.PC_TO_ANDROID, barrierSeed = 63)
        assertTrue(capability.coordinator.process(capability.actor, capability.seal(
            TYPE_PROPOSAL, proposalPayload(capabilityProposal), sequence = 1,
        )))
        capability.lane.negotiated.value = DirectTransportSignalingNegotiatedCapabilities(
            SIGNAL_GENERATION,
            emptySet(),
        )
        assertFalse(capability.coordinator.process(capability.actor, capability.seal(
            TYPE_FALLBACK, fallbackPayload(capabilityProposal, 4), sequence = 2,
        )))
        assertEquals(1, capability.native.receivedFallbacks.size)
        assertFalse(capability.lane.closed.get())
    }

    @Test
    fun proposalAcceptCommitAndEchoAdvanceBothDirectionsAndEchoOnlyAfterSend() {
        val fixture = directFixture()
        val value = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 70)
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL, proposalPayload(value), sequence = 1,
        )))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.ACCEPT, value, accepted = true),
        ))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT, commitPayload(value), sequence = 2, acknowledgement = 1,
        )))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.COMMIT, value, isEcho = true),
        ))

        assertEquals(1, fixture.native.receivedProposals.size)
        assertEquals(1, fixture.native.receivedCommits.size)
        assertEquals(1, fixture.native.commitEchoes.size)
        assertEquals(listOf(TYPE_ACCEPT, TYPE_COMMIT), fixture.lane.sent.map { it.signalType })
        assertEquals(listOf(1L, 2L), fixture.lane.sent.map { it.signalSeq })
        assertEquals(listOf(1L, 2L), fixture.lane.sent.map { it.signalAck })

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK, fallbackPayload(value, 4), sequence = 3, acknowledgement = 2,
        )))
        assertEquals(1, fixture.native.receivedFallbacks.size)
        assertEquals(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            fixture.native.receivedFallbacks.single().direction,
        )

        val sendFailure = directFixture()
        val failedValue = proposal(sendFailure, Direction.PC_TO_ANDROID, barrierSeed = 71)
        sendFailure.lane.acceptSends.set(false)
        assertFalse(sendFailure.coordinator.processNativeAction(
            sendFailure.actor,
            action(DirectNativeRouteActionKindV5.COMMIT, failedValue, isEcho = true),
        ))
        assertTrue(sendFailure.native.commitEchoes.isEmpty())
        sendFailure.lane.acceptSends.set(true)
        assertTrue(sendFailure.coordinator.processNativeAction(
            sendFailure.actor,
            action(DirectNativeRouteActionKindV5.COMMIT, failedValue, isEcho = true),
        ))
        assertEquals(1, sendFailure.native.commitEchoes.size)
        assertEquals(1, sendFailure.lane.sent.single().signalSeq)
    }

    @Test
    fun rejectedAcceptClearsOnlyTheOutboundNegotiationAndProjectsRelay() {
        val observations = CopyOnWriteArrayList<CutoverObservation>()
        val fixture = directFixture(observations)
        val value = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 64)
        assertTrue(fixture.coordinator.startCutover(fixture.actor, value))

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_ACCEPT,
            rejectedAcceptPayload(value),
            sequence = 1,
            acknowledgement = 1,
        )))

        assertFalse(fixture.native.receivedAccepts.single().accepted)
        val state = assertNotNull(field<MutableMap<UUID, Any>>(fixture.coordinator, "peers")[PEER])
        assertTrue(field<MutableMap<*, *>>(state, "barriers").isEmpty())
        assertEquals(DirectNativeDirectionV5.ANDROID_TO_PC, observations.single().direction)
        assertTrue(observations.single().activeSessionIds.isEmpty())
        assertFalse(observations.single().committed)
    }

    @Test
    fun committedBarrierIdCannotBeReusedByAnotherBatchOrDirection() {
        val fixture = directFixture()
        val committed = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 65)
        commitOutbound(fixture, committed, remoteSequence = 0)

        val duplicate = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 65)
        assertFalse(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL,
            proposalPayload(duplicate),
            sequence = 3,
            acknowledgement = 1,
        )))
        assertTrue(fixture.native.receivedProposals.isEmpty())
    }

    @Test
    fun proposerRetainsCommittedBarrierAsFallbackAuthorityUntilPathRetirement() {
        val fixture = directFixture()
        val value = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 77)
        assertTrue(fixture.coordinator.startCutover(fixture.actor, value))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_ACCEPT, acceptPayload(value), sequence = 1, acknowledgement = 1,
        )))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT, commitPayload(value), sequence = 2, acknowledgement = 1,
        )))

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK, fallbackPayload(value, 4), sequence = 3, acknowledgement = 1,
        )))
        assertEquals(1, fixture.native.receivedFallbacks.size)
        assertEquals(
            DirectNativeDirectionV5.ANDROID_TO_PC,
            fixture.native.receivedFallbacks.single().direction,
        )
    }

    @Test
    fun commitMatchesExactBarrierIdentityAndDispatchesThePeerEchoDirection() {
        val fixture = directFixture()
        val outbound = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 72)
        val inbound = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 73)
        assertFalse(uuidBytes(outbound.barrierId).contentEquals(uuidBytes(inbound.barrierId)))
        assertFalse(outbound.barrierSha256.contentEquals(inbound.barrierSha256))

        assertTrue(fixture.coordinator.startCutover(fixture.actor, outbound))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL,
            proposalPayload(inbound),
            sequence = 1,
        )))

        val wrongId = uuid(74)
        assertFalse(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT,
            commitPayload(wrongId, outbound.barrierSha256),
            sequence = 2,
            acknowledgement = 1,
        )))
        assertFalse(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT,
            commitPayload(outbound.barrierId, ByteArray(32) { 99 }),
            sequence = 2,
            acknowledgement = 1,
        )))
        assertTrue(fixture.native.receivedCommits.isEmpty())

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT,
            commitPayload(outbound),
            sequence = 2,
            acknowledgement = 1,
        )))
        val echo = fixture.native.receivedCommits.single()
        assertEquals(DirectNativeDirectionV5.ANDROID_TO_PC, echo.direction)
        assertTrue(echo.isEcho)
        assertContentEquals(uuidBytes(outbound.barrierId), echo.barrierId)
        assertContentEquals(outbound.barrierSha256, echo.barrierSha256)

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT,
            commitPayload(inbound),
            sequence = 3,
            acknowledgement = 1,
        )))
        val initial = fixture.native.receivedCommits.last()
        assertEquals(DirectNativeDirectionV5.PC_TO_ANDROID, initial.direction)
        assertFalse(initial.isEcho)
        assertContentEquals(uuidBytes(inbound.barrierId), initial.barrierId)
        assertContentEquals(inbound.barrierSha256, initial.barrierSha256)
        assertEquals(2, fixture.native.receivedCommits.size)
    }

    @Test
    fun fallbackMatchesOneDirectionByBarrierIdAndRejectsAnAmbiguousId() {
        val fixture = directFixture()
        val outbound = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 74)
        val inbound = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 75)
        assertTrue(fixture.coordinator.startCutover(fixture.actor, outbound))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL,
            proposalPayload(inbound),
            sequence = 1,
        )))

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK,
            fallbackPayload(outbound, 3),
            sequence = 2,
            acknowledgement = 1,
        )))
        assertEquals(DirectNativeDirectionV5.ANDROID_TO_PC, fixture.native.receivedFallbacks.single().direction)

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK,
            fallbackPayload(outbound, 3),
            sequence = 3,
            acknowledgement = 1,
        )))
        assertEquals(1, fixture.native.receivedFallbacks.size)

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK,
            fallbackPayload(inbound, 4),
            sequence = 4,
            acknowledgement = 1,
        )))
        assertEquals(DirectNativeDirectionV5.PC_TO_ANDROID, fixture.native.receivedFallbacks.last().direction)

        val ambiguous = directFixture()
        val ambiguousOutbound = proposal(ambiguous, Direction.ANDROID_TO_PC, barrierSeed = 76)
        val ambiguousInbound = proposal(ambiguous, Direction.PC_TO_ANDROID, barrierSeed = 76)
        assertTrue(ambiguous.coordinator.startCutover(ambiguous.actor, ambiguousOutbound))
        assertFalse(ambiguous.coordinator.process(ambiguous.actor, ambiguous.seal(
            TYPE_PROPOSAL,
            proposalPayload(ambiguousInbound),
            sequence = 1,
        )))
        assertTrue(ambiguous.native.receivedFallbacks.isEmpty())
    }

    @Test
    fun negotiatingFallbackCancelsOnlyItsBatchButCommittedFallbackClearsTheDirection() {
        val observations = CopyOnWriteArrayList<CutoverObservation>()
        val fixture = directFixture(observations)
        val first = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 100)
        val second = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 101)
        val negotiating = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 102)
        val later = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 103)

        commitOutbound(fixture, first, remoteSequence = 0)
        commitOutbound(fixture, second, remoteSequence = 2)
        assertTrue(fixture.coordinator.startCutover(fixture.actor, negotiating))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK,
            fallbackPayload(negotiating, 4),
            sequence = 5,
            acknowledgement = 3,
        )))
        assertEquals(
            setOf(uuid(200), uuid(201)),
            observations.last().activeSessionIds,
        )

        assertTrue(fixture.coordinator.startCutover(fixture.actor, later))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK,
            fallbackPayload(first, 4),
            sequence = 6,
            acknowledgement = 4,
        )))
        assertTrue(observations.last().activeSessionIds.isEmpty())
        assertFalse(observations.last().committed)
        val state = assertNotNull(field<MutableMap<UUID, Any>>(fixture.coordinator, "peers")[PEER])
        assertTrue(field<MutableMap<*, *>>(state, "barriers").isEmpty())
        assertTrue(field<MutableList<*>>(state, "committedBarriers").isEmpty())
    }

    @Test
    fun committedFallbackClearsEveryBatchInItsDirectionAndDuplicateKeepsOppositeDirection() {
        val observations = CopyOnWriteArrayList<CutoverObservation>()
        val fixture = directFixture(observations)
        val firstOutbound = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 120)
        val secondOutbound = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 121)
        val inbound = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 122)

        commitOutbound(fixture, firstOutbound, remoteSequence = 0)
        commitOutbound(fixture, secondOutbound, remoteSequence = 2)
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL, proposalPayload(inbound), sequence = 5, acknowledgement = 2,
        )))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.ACCEPT, inbound, accepted = true),
        ))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT, commitPayload(inbound), sequence = 6, acknowledgement = 3,
        )))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.COMMIT, inbound, isEcho = true),
        ))

        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK, fallbackPayload(secondOutbound, 4), sequence = 7, acknowledgement = 4,
        )))
        assertEquals(1, fixture.native.receivedFallbacks.size)
        assertEquals(DirectNativeDirectionV5.ANDROID_TO_PC, observations.last().direction)
        assertTrue(observations.last().activeSessionIds.isEmpty())

        val state = assertNotNull(field<MutableMap<UUID, Any>>(fixture.coordinator, "peers")[PEER])
        val committed = field<MutableList<Any>>(state, "committedBarriers")
        assertEquals(1, committed.size)
        assertEquals(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            field<DirectNativeDirectionV5>(committed.single(), "direction"),
        )
        val observationCount = observations.size

        // A semantically duplicated fallback may carry a fresh signalling
        // sequence after reconnect/retry. It is idempotent and cannot retire
        // the still-healthy opposite direction.
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_FALLBACK, fallbackPayload(secondOutbound, 4), sequence = 8, acknowledgement = 4,
        )))
        assertEquals(1, fixture.native.receivedFallbacks.size)
        assertEquals(observationCount, observations.size)
        assertEquals(1, committed.size)
        assertEquals(
            DirectNativeDirectionV5.PC_TO_ANDROID,
            field<DirectNativeDirectionV5>(committed.single(), "direction"),
        )
    }

    @Test
    fun nativeDirectionFallbackSendsOneCommittedRepresentativeAndNeverAnEmptyWireId() {
        val observations = CopyOnWriteArrayList<CutoverObservation>()
        val fixture = directFixture(observations)
        val first = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 110)
        val second = proposal(fixture, Direction.ANDROID_TO_PC, barrierSeed = 111)
        commitOutbound(fixture, first, remoteSequence = 0)
        commitOutbound(fixture, second, remoteSequence = 2)
        val sendsBefore = fixture.lane.sent.size

        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.FALLBACK, second, reason = 1)
                .copy(barrierId = ByteArray(16)),
        ))

        assertEquals(sendsBefore + 1, fixture.lane.sent.size)
        val plaintext = DirectTransportCrypto.decryptSignal(PAIRWISE_KEY, fixture.lane.sent.last())
        try {
            val fallback = TransportSignalPayloadV1.parseFrom(plaintext).fallback
            assertContentEquals(uuidBytes(first.barrierId), fallback.barrierId.toByteArray())
            assertEquals(16, fallback.barrierId.size())
        } finally {
            plaintext.fill(0)
        }
        assertTrue(observations.last().activeSessionIds.isEmpty())
    }

    @Test
    fun fallbackFrameBoundaryAllowsZeroThreeAndFourPlusButRejectsReservedAndNegative() {
        val cases = listOf(
            0L to true,
            1L to false,
            2L to false,
            3L to true,
            4L to true,
            Long.MAX_VALUE to true,
            -1L to false,
        )
        for ((lastFrame, expected) in cases) {
            val fixture = directFixture()
            val value = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 80)
            assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
                TYPE_PROPOSAL, proposalPayload(value), sequence = 1,
            )))
            assertEquals(
                expected,
                fixture.coordinator.process(fixture.actor, fixture.seal(
                    TYPE_FALLBACK,
                    fallbackPayload(value, lastFrame),
                    sequence = 2,
                )),
                "last_received_direct_frame_seq=$lastFrame",
            )
            assertEquals(if (expected) 1 else 0, fixture.native.receivedFallbacks.size)
        }
    }

    @Test
    fun pathCloseRetiresOnlyItsPeerAndNeverRestartsSharedRelay(): Unit = runBlocking {
        val fixture = runtimeFixture()
        val first = installPeer(fixture, uuid(10), path(10, 30, 31))
        val second = installPeer(fixture, uuid(11), path(11, 32, 33))

        fixture.lane.emit(sealedInbound(
            peer = uuid(10),
            path = first.path,
            payload = closePayload(),
            type = TYPE_CLOSE,
            sequence = 1,
        ))
        withTimeout(2_000) {
            while (!first.closed.get()) delay(1)
        }

        assertFalse(uuid(10) in fixture.runtime.carriers.value)
        assertTrue(uuid(11) in fixture.runtime.carriers.value)
        assertFalse(second.closed.get())
        assertEquals(0, fixture.socket.disconnects.get())
        assertFalse(fixture.lane.closed.get())
        assertTrue(fixture.runtime.updateAppliedCursor(uuid(11), uuid(90), 1))
        withTimeout(2_000) {
            while (second.cursorUpdates.get() != 1) delay(1)
        }
    }

    @Test
    fun malformedAndOverflowingPeerFailuresRemainPeerLocalAndLeaveRelayRunning(): Unit = runBlocking {
        val fixture = runtimeFixture(queueFrames = 1)
        val malformed = installPeer(fixture, uuid(12), path(12, 34, 35))
        val survivor = installPeer(fixture, uuid(13), path(13, 36, 37))
        val malformedProposal = proposalFor(
            path = malformed.path,
            direction = Direction.PC_TO_ANDROID,
            binding = DIRECT_PATH_BINDING,
            barrierSeed = 91,
        )
        val malformedBytes = proposalPayload(malformedProposal).toByteArray() +
            byteArrayOf(0xf8.toByte(), 0x07, 0x01)
        fixture.lane.emit(sealedInboundRaw(
            peer = uuid(12),
            path = malformed.path,
            plaintext = malformedBytes,
            type = TYPE_PROPOSAL,
            sequence = 1,
        ))
        withTimeout(2_000) {
            while (!malformed.closed.get()) delay(1)
        }
        assertFalse(uuid(12) in fixture.runtime.carriers.value)
        assertTrue(uuid(13) in fixture.runtime.carriers.value)

        val overflowing = installPeer(fixture, uuid(14), path(14, 38, 39))
        overflowing.blockProposal = true
        val overflowProposal = proposalFor(
            path = overflowing.path,
            direction = Direction.PC_TO_ANDROID,
            binding = DIRECT_PATH_BINDING,
            barrierSeed = 92,
        )
        fixture.lane.emit(sealedInbound(
            peer = uuid(14), path = overflowing.path, payload = proposalPayload(overflowProposal),
            type = TYPE_PROPOSAL, sequence = 1,
        ))
        assertTrue(overflowing.proposalEntered.await(2, TimeUnit.SECONDS))
        fixture.lane.emit(sealedInbound(
            peer = uuid(14), path = overflowing.path, payload = proposalPayload(overflowProposal),
            type = TYPE_PROPOSAL, sequence = 2,
        ))
        fixture.lane.emit(sealedInbound(
            peer = uuid(14), path = overflowing.path, payload = proposalPayload(overflowProposal),
            type = TYPE_PROPOSAL, sequence = 3,
        ))
        withTimeout(2_000) {
            while (uuid(14) in fixture.runtime.carriers.value) delay(1)
        }
        overflowing.releaseProposal.countDown()
        withTimeout(2_000) {
            while (!overflowing.closed.get()) delay(1)
        }

        assertTrue(uuid(13) in fixture.runtime.carriers.value)
        assertFalse(survivor.closed.get())
        assertTrue(fixture.runtime.updateAppliedCursor(uuid(13), uuid(93), 2))
        withTimeout(2_000) {
            while (survivor.cursorUpdates.get() != 1) delay(1)
        }
        assertEquals(0, fixture.socket.disconnects.get())
        assertEquals(0, fixture.socket.connects.get())
        assertFalse(fixture.lane.closed.get())
    }

    @Test
    fun closeClearsRegistrationKeysPeerKeysAndEveryBarrier() {
        val lane = FakeOwnedSignalLane()
        val coordinator = ProductionDirectDataCoordinator(
            LOCAL, lane, scope, clockMillis = { NOW }, random = FixedSecureRandom(),
        )
        closeables += coordinator
        val native = FakeNative(defaultPath())
        val fence = fence(native.path)
        val registration = registration(PEER, fence)
        val registrationKey = registration.keyId
        val registrationPairwise = registration.pairwiseKey
        val registrationBinding = registration.directPathBinding
        assertTrue(coordinator.registerPeer(registration))
        assertAllZero(registrationKey)
        assertAllZero(registrationPairwise)
        assertAllZero(registrationBinding)

        val actor = FakeActor(PEER, fence, KEY_EPOCH, native)
        val value = proposalFor(native.path, Direction.ANDROID_TO_PC, DIRECT_PATH_BINDING, 94)
        assertTrue(coordinator.startCutover(actor, value))

        val peers = field<MutableMap<UUID, Any>>(coordinator, "peers")
        val state = assertNotNull(peers[PEER])
        val stateKey = field<ByteArray>(state, "keyId")
        val statePairwise = field<ByteArray>(state, "pairwiseKey")
        val stateBinding = field<ByteArray>(state, "directPathBinding")
        val barriers = field<MutableMap<*, Any>>(state, "barriers")
        val barrier = assertNotNull(barriers.values.singleOrNull())
        val barrierId = field<ByteArray>(barrier, "id")
        val barrierSha256 = field<ByteArray>(barrier, "sha256")

        coordinator.close()

        assertTrue(peers.isEmpty())
        assertTrue(barriers.isEmpty())
        listOf(stateKey, statePairwise, stateBinding, barrierId, barrierSha256).forEach(::assertAllZero)
        assertTrue(lane.closed.get())
    }

    @Test
    fun closeClearsCommittedFallbackAuthority() {
        val fixture = directFixture()
        val value = proposal(fixture, Direction.PC_TO_ANDROID, barrierSeed = 95)
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_PROPOSAL, proposalPayload(value), sequence = 1,
        )))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT, commitPayload(value), sequence = 2,
        )))
        assertTrue(fixture.coordinator.processNativeAction(
            fixture.actor,
            action(DirectNativeRouteActionKindV5.COMMIT, value, isEcho = true),
        ))

        val peers = field<MutableMap<UUID, Any>>(fixture.coordinator, "peers")
        val state = assertNotNull(peers[PEER])
        val committed = field<MutableList<Any>>(state, "committedBarriers")
        val barrier = assertNotNull(committed.singleOrNull())
        val barrierId = field<ByteArray>(barrier, "id")
        val barrierSha256 = field<ByteArray>(barrier, "sha256")

        fixture.coordinator.close()

        assertTrue(committed.isEmpty())
        assertAllZero(barrierId)
        assertAllZero(barrierSha256)
    }

    private fun directFixture(
        observations: MutableList<CutoverObservation> = mutableListOf(),
        localSequence: Long = 0,
        remoteSequence: Long = 0,
        remoteAcknowledgement: Long = 0,
    ): DirectFixture {
        val lane = FakeOwnedSignalLane()
        val coordinator = ProductionDirectDataCoordinator(
            LOCAL,
            lane,
            scope,
            clockMillis = { NOW },
            random = FixedSecureRandom(),
            cutoverObserver = ProductionDirectCutoverObserver {
                    _, _, direction, activeSessionIds, committed ->
                observations += CutoverObservation(direction, activeSessionIds, committed)
            },
        )
        closeables += coordinator
        val native = FakeNative(defaultPath())
        val fence = fence(native.path)
        val actor = FakeActor(PEER, fence, KEY_EPOCH, native)
        assertTrue(coordinator.registerPeer(registration(
            peer = PEER,
            pathFence = fence,
            localSequence = localSequence,
            remoteSequence = remoteSequence,
            remoteAcknowledgement = remoteAcknowledgement,
        )))
        return DirectFixture(coordinator, lane, native, actor, fence)
    }

    private fun commitOutbound(
        fixture: DirectFixture,
        proposal: ProductionDirectCutoverProposal,
        remoteSequence: Long,
    ) {
        assertTrue(fixture.coordinator.startCutover(fixture.actor, proposal))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_ACCEPT,
            acceptPayload(proposal),
            sequence = remoteSequence + 1,
            acknowledgement = remoteSequence / 2 + 1,
        )))
        assertTrue(fixture.coordinator.process(fixture.actor, fixture.seal(
            TYPE_COMMIT,
            commitPayload(proposal),
            sequence = remoteSequence + 2,
            acknowledgement = remoteSequence / 2 + 1,
        )))
    }

    private fun runtimeFixture(
        attach: Boolean = true,
        queueFrames: Int = 8,
    ): RuntimeFixture {
        val lane = FakeOwnedSignalLane()
        val coordinator = ProductionDirectDataCoordinator(
            LOCAL, lane, scope, clockMillis = { NOW }, random = FixedSecureRandom(),
        )
        val socket = FakeSocket()
        val multiplexer = ProductionRealtimeMultiplexer.forTest(socket, scope)
        val realtime = MutableStateFlow(
            ProductionRealtimeNegotiatedCapabilities(
                RELAY_GENERATION,
                setOf(DATA_CAPABILITY, SESSION_CUTOVER_CAPABILITY),
            ),
        )
        val runtime = ProductionDirectDataRuntime(
            multiplexer = multiplexer,
            realtimeCapabilities = realtime,
            signaling = coordinator,
            relaySender = ProductionDirectRelayExactSender { _, _ -> true },
            signalProcessor = coordinator,
            signer = ProductionDirectDataSigner { ByteArray(64) { 1 } },
            parentScope = scope,
            enabled = true,
            clockMillis = { NOW },
            queueFramesPerPeer = queueFrames,
            queueBytesPerPeer = 1024 * 1024,
            pumpIntervalMillis = 30_000,
        )
        closeables += multiplexer
        closeables += runtime
        closeables += coordinator
        if (attach) assertTrue(coordinator.attachRuntime(runtime))
        return RuntimeFixture(coordinator, runtime, lane, socket)
    }

    private suspend fun installPeer(
        fixture: RuntimeFixture,
        peer: UUID,
        path: DirectNativePathV5,
    ): FakeNative {
        val native = FakeNative(path)
        val fence = fence(path)
        assertTrue(fixture.coordinator.registerPeer(registration(peer, fence)))
        assertTrue(fixture.runtime.installPromotedPeer(
            ProductionDirectDataPeerInstall(
                peer,
                fence,
                KEY_EPOCH,
                native,
                fixture.runtime.captureGate(serverRoutingPermitted = true, keyEpoch = KEY_EPOCH),
            ),
        ))
        withTimeout(2_000) {
            while (peer !in fixture.runtime.carriers.value) delay(1)
        }
        return native
    }

    private fun handoff(
        fixture: RuntimeFixture,
        peer: UUID,
        path: DirectNativePathV5,
        source: ProductionDirectV4ReadySource,
    ) = ProductionDirectV4ReadyHandoff(
        registration(peer, fence(path)),
        nomination(path),
        fixture.runtime.captureGate(serverRoutingPermitted = true, keyEpoch = KEY_EPOCH),
        source,
    )

    private fun registration(
        peer: UUID,
        pathFence: ProductionDirectPathFence,
        localSequence: Long = 0,
        remoteSequence: Long = 0,
        remoteAcknowledgement: Long = 0,
    ) = ProductionDirectCutoverPeerRegistration(
        peer,
        pathFence,
        KEY_EPOCH,
        KEY_ID,
        PAIRWISE_KEY,
        DIRECT_PATH_BINDING,
        SIGNAL_GENERATION,
        localSequence,
        remoteSequence,
        remoteAcknowledgement,
    )

    private fun proposal(
        fixture: DirectFixture,
        direction: Direction,
        barrierSeed: Int,
    ) = proposalFor(fixture.native.path, direction, DIRECT_PATH_BINDING, barrierSeed)

    private fun proposalFor(
        path: DirectNativePathV5,
        direction: Direction,
        binding: ByteArray,
        barrierSeed: Int,
    ) = ProductionDirectCutoverCodec.createProposal(
        connectionId = path.connectionId.toUuid(),
        pathEpoch = path.epoch,
        direction = direction,
        barrierId = uuid(barrierSeed),
        directPathBinding = binding,
        sessionBarriers = listOf(ProductionDirectSessionCutoverBarrier(uuid(barrierSeed + 100), 4)),
        duplicateGraceMilliseconds = 500,
    )

    private fun proposalPayload(value: ProductionDirectCutoverProposal) =
        TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(1)
            .setCutoverProposal(
                dev.termlink.protocol.transport.v1.DirectCutoverProposalV1.parseFrom(value.wireBytes),
            )
            .build()

    private fun acceptPayload(value: ProductionDirectCutoverProposal) =
        TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(1)
            .setCutoverAccept(
                DirectCutoverAcceptV1.newBuilder()
                    .setBarrierId(ByteString.copyFrom(uuidBytes(value.barrierId)))
                    .setBarrierSha256(ByteString.copyFrom(value.barrierSha256))
                    .setResult(DirectPathResultV1.DIRECT_PATH_RESULT_V1_ACCEPTED),
            )
            .build()

    private fun rejectedAcceptPayload(value: ProductionDirectCutoverProposal) =
        TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(1)
            .setCutoverAccept(
                DirectCutoverAcceptV1.newBuilder()
                    .setBarrierId(ByteString.copyFrom(uuidBytes(value.barrierId)))
                    .setBarrierSha256(ByteString.copyFrom(value.barrierSha256))
                    .setResult(DirectPathResultV1.DIRECT_PATH_RESULT_V1_REJECTED)
                    .setReasonCode("DIRECT_NATIVE_REASON_1"),
            )
            .build()

    private fun commitPayload(value: ProductionDirectCutoverProposal) =
        commitPayload(value.barrierId, value.barrierSha256)

    private fun commitPayload(barrierId: UUID, barrierSha256: ByteArray) =
        TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(1)
            .setCutoverCommit(
                DirectCutoverCommitV1.newBuilder()
                    .setBarrierId(ByteString.copyFrom(uuidBytes(barrierId)))
                    .setBarrierSha256(ByteString.copyFrom(barrierSha256)),
            )
            .build()

    private fun fallbackPayload(value: ProductionDirectCutoverProposal, lastFrame: Long) =
        TransportSignalPayloadV1.newBuilder()
            .setFormatVersion(1)
            .setFallback(
                DirectFallbackV1.newBuilder()
                    .setBarrierId(ByteString.copyFrom(uuidBytes(value.barrierId)))
                    .setReasonCode("DIRECT_NATIVE_REASON_1")
                    .setLastReceivedDirectFrameSeq(lastFrame),
            )
            .build()

    private fun closePayload() = TransportSignalPayloadV1.newBuilder()
        .setFormatVersion(1)
        .setPathClose(DirectPathCloseV1.newBuilder().setReasonCode("DIRECT_NATIVE_REASON_1"))
        .build()

    private fun action(
        kind: DirectNativeRouteActionKindV5,
        value: ProductionDirectCutoverProposal,
        accepted: Boolean = false,
        isEcho: Boolean = false,
        reason: Int = 0,
    ) = DirectNativeRouteActionV5(
        path = defaultPath(),
        kind = kind,
        direction = if (value.direction == Direction.PC_TO_ANDROID) {
            DirectNativeDirectionV5.PC_TO_ANDROID
        } else {
            DirectNativeDirectionV5.ANDROID_TO_PC
        },
        sessionId = ByteArray(16),
        envelopeSequence = 0,
        directFrameSequence = 0,
        barrierId = uuidBytes(value.barrierId),
        barrierSha256 = value.barrierSha256,
        accepted = accepted,
        isEcho = isEcho,
        carrier = null,
        flags = 0,
        reason = reason,
        payloadBytes = 0,
        payload = ByteArray(0),
    )

    private fun sealedInbound(
        peer: UUID,
        path: DirectNativePathV5,
        payload: TransportSignalPayloadV1,
        type: TransportSignalTypeV1,
        sequence: Long,
        acknowledgement: Long = 0,
    ) = sealedInboundRaw(peer, path, payload.toByteArray(), type, sequence, acknowledgement)

    private fun sealedInboundRaw(
        peer: UUID,
        path: DirectNativePathV5,
        plaintext: ByteArray,
        type: TransportSignalTypeV1,
        sequence: Long,
        acknowledgement: Long = 0,
        mutate: (TransportSignalEnvelopeV1.Builder) -> Unit = {},
    ): TransportSignalEnvelopeV1 {
        val metadata = TransportSignalEnvelopeV1.newBuilder()
            .setProtocolVersion(1)
            .setSignalType(type)
            .setConnectionId(ByteString.copyFrom(path.connectionId))
            .setSenderDeviceId(ByteString.copyFrom(uuidBytes(peer)))
            .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(LOCAL)))
            .setPathEpoch(path.epoch)
            .setSignalSeq(sequence)
            .setSignalAck(acknowledgement)
            .setSentAtMs(NOW)
            .setKeyId(ByteString.copyFrom(KEY_ID))
        mutate(metadata)
        return DirectTransportCrypto.encryptSignal(
            PAIRWISE_KEY,
            metadata.build(),
            plaintext,
            FixedSecureRandom(),
        )
    }

    private fun nomination(path: DirectNativePathV5): DirectNativeCommitNominationRequestV4 {
        fun candidate(seed: Int) = DirectNativeCandidateV4(
            ByteArray(16) { seed.toByte() },
            ByteArray(16),
            DirectNativeSocketAddressV4(byteArrayOf(10, 0, 0, seed.toByte()), 50_000 + seed),
            DirectNativeCandidateKindV4.HOST,
            2_130_706_431,
            10,
            NOW + 10_000,
        )
        return DirectNativeCommitNominationRequestV4(
            epoch = path.epoch,
            attemptId = path.attemptId,
            checkRoundId = ByteArray(16) { 11 },
            nominationId = ByteArray(16) { 12 },
            winningPair = DirectNativeCandidatePairV4(1, candidate(1), candidate(2)),
            role = DirectNativeRoleV4.INITIATOR,
            timeoutMillis = 2_000,
            selectionConfirmed = true,
            peerCertificateSha256 = ByteArray(32) { 13 },
            peerDeviceSigningPublicKey = ByteArray(32) { 14 },
            localProofNonce = ByteArray(16) { 15 },
            nowMillis = NOW,
        )
    }

    private data class DirectFixture(
        val coordinator: ProductionDirectDataCoordinator,
        val lane: FakeOwnedSignalLane,
        val native: FakeNative,
        val actor: FakeActor,
        val fence: ProductionDirectPathFence,
    ) {
        fun seal(
            type: TransportSignalTypeV1,
            payload: TransportSignalPayloadV1,
            sequence: Long,
            acknowledgement: Long = 0,
            mutate: (TransportSignalEnvelopeV1.Builder) -> Unit = {},
        ) = sealRaw(type, payload.toByteArray(), sequence, acknowledgement, mutate)

        fun sealRaw(
            type: TransportSignalTypeV1,
            plaintext: ByteArray,
            sequence: Long,
            acknowledgement: Long = 0,
            mutate: (TransportSignalEnvelopeV1.Builder) -> Unit = {},
        ): TransportSignalEnvelopeV1 {
            val metadata = TransportSignalEnvelopeV1.newBuilder()
                .setProtocolVersion(1)
                .setSignalType(type)
                .setConnectionId(ByteString.copyFrom(native.path.connectionId))
                .setSenderDeviceId(ByteString.copyFrom(uuidBytes(actor.peerDeviceId)))
                .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(LOCAL)))
                .setPathEpoch(fence.pathEpoch)
                .setSignalSeq(sequence)
                .setSignalAck(acknowledgement)
                .setSentAtMs(NOW)
                .setKeyId(ByteString.copyFrom(KEY_ID))
            mutate(metadata)
            return DirectTransportCrypto.encryptSignal(
                PAIRWISE_KEY, metadata.build(), plaintext, FixedSecureRandom(),
            )
        }
    }

    private data class CutoverObservation(
        val direction: DirectNativeDirectionV5,
        val activeSessionIds: Set<UUID>,
        val committed: Boolean,
    )

    private data class RuntimeFixture(
        val coordinator: ProductionDirectDataCoordinator,
        val runtime: ProductionDirectDataRuntime,
        val lane: FakeOwnedSignalLane,
        val socket: FakeSocket,
    )

    private data class FakeActor(
        override val peerDeviceId: UUID,
        override val pathFence: ProductionDirectPathFence,
        override val keyEpoch: Long,
        override val native: ProductionDirectDataNative,
    ) : ProductionDirectDataPeerBoundary

    private class FakeOwnedSignalLane : ProductionDirectOwnedSignalLane {
        private val source = MutableSharedFlow<TransportSignalEnvelopeV1>(extraBufferCapacity = 512)
        val collectionStarts = AtomicInteger(0)
        val negotiated = MutableStateFlow(capabilities())
        val sent = CopyOnWriteArrayList<TransportSignalEnvelopeV1>()
        val sendCalls = AtomicInteger(0)
        val acceptSends = AtomicBoolean(true)
        val closed = AtomicBoolean(false)

        override val incoming: Flow<TransportSignalEnvelopeV1> = flow {
            collectionStarts.incrementAndGet()
            source.collect { emit(it) }
        }
        override val negotiatedCapabilities: StateFlow<DirectTransportSignalingNegotiatedCapabilities> = negotiated

        override fun send(envelope: TransportSignalEnvelopeV1): Boolean {
            sendCalls.incrementAndGet()
            if (!acceptSends.get()) return false
            sent += envelope
            return true
        }

        suspend fun emit(envelope: TransportSignalEnvelopeV1) {
            source.emit(envelope)
        }

        override fun close() {
            closed.set(true)
        }
    }

    private class FakeV4Source(
        private val result: (DirectNativePathV5) -> DirectNativeResultV5<ProductionDirectDataNative>,
    ) : ProductionDirectV4ReadySource {
        val promoteCalls = AtomicInteger(0)
        val closes = AtomicInteger(0)

        override fun promote(
            path: DirectNativePathV5,
            nomination: DirectNativeCommitNominationRequestV4,
        ): DirectNativeResultV5<ProductionDirectDataNative> {
            promoteCalls.incrementAndGet()
            return result(path)
        }

        override fun close() {
            closes.incrementAndGet()
        }
    }

    private class FakeNative(override val path: DirectNativePathV5) : ProductionDirectDataNative {
        val startedProposals = CopyOnWriteArrayList<DirectNativeProposalV5>()
        val receivedProposals = CopyOnWriteArrayList<DirectNativeProposalV5>()
        val receivedAccepts = CopyOnWriteArrayList<DirectNativeTransitionV5>()
        val receivedCommits = CopyOnWriteArrayList<DirectNativeTransitionV5>()
        val commitEchoes = CopyOnWriteArrayList<DirectNativeTransitionV5>()
        val receivedFallbacks = CopyOnWriteArrayList<DirectNativeTransitionV5>()
        val cursorUpdates = AtomicInteger(0)
        val cancels = AtomicInteger(0)
        val closed = AtomicBoolean(false)
        val proposalEntered = CountDownLatch(1)
        val releaseProposal = CountDownLatch(1)
        @Volatile var blockProposal = false

        override fun pathInfo() = DirectNativeResultV5.Success(
            DirectNativePathInfoV5(path, DirectNativePathKindV5.LAN, true, DIRECT_PATH_BINDING),
        )

        override fun startProposal(value: DirectNativeProposalV5): DirectNativeResultV5<Unit> {
            startedProposals += value
            return ok()
        }

        override fun receiveProposal(value: DirectNativeProposalV5): DirectNativeResultV5<Unit> {
            receivedProposals += value
            proposalEntered.countDown()
            if (blockProposal) releaseProposal.await(2, TimeUnit.SECONDS)
            return ok()
        }

        override fun receiveAccept(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit> {
            receivedAccepts += value
            return ok()
        }

        override fun receiveCommit(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit> {
            receivedCommits += value
            return ok()
        }

        override fun commitEchoSent(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit> {
            commitEchoes += value
            return ok()
        }

        override fun receiveFallback(value: DirectNativeTransitionV5): DirectNativeResultV5<Unit> {
            receivedFallbacks += value
            return ok()
        }

        override fun tick(value: DirectNativeTransitionV5) = ok()
        override fun updateAppliedCursor(value: DirectNativeCursorV5): DirectNativeResultV5<Unit> {
            cursorUpdates.incrementAndGet()
            return ok()
        }
        override fun deregisterSession(value: DirectNativeCursorV5): DirectNativeResultV5<Unit> = ok()
        override fun routeEnvelope(value: DirectNativeRouteEnvelopeV5, exactEnvelope: ByteArray) = ok()
        override fun arbitrateIngress(value: DirectNativeIngressV5, exactEnvelope: ByteArray) = ok()
        override fun tickIngress(nowMillis: Long) = ok()
        override fun pollRouteAction() = DirectNativeResultV5.Success<DirectNativeRouteActionV5?>(null)
        override fun pollEvent() = DirectNativeResultV5.Success<DirectNativeEventV5?>(null)
        override fun receiveSigningPayload() = DirectNativeResultV5.Success<DirectNativeSigningPayloadV5?>(null)
        override fun submitSignature(requestId: Long, signature: ByteArray) = ok()
        override fun receiveEnvelope() = DirectNativeResultV5.Success<DirectNativeDataEnvelope?>(null)
        override fun cancel(): DirectNativeResultV5<Unit> {
            cancels.incrementAndGet()
            return ok()
        }
        override fun networkChanged() = ok()
        override fun close() {
            closed.set(true)
        }

        fun totalCutoverCalls() = startedProposals.size + receivedProposals.size + receivedAccepts.size +
            receivedCommits.size + commitEchoes.size + receivedFallbacks.size

        private fun ok(): DirectNativeResultV5<Unit> = DirectNativeResultV5.Success(Unit)
    }

    private class FakeSocket : ProductionRealtimeSocket {
        override val receivedEnvelopes = kotlinx.coroutines.flow.emptyFlow<ProductionReceivedEnvelope>()
        override val receivePressure: StateFlow<ProductionReceivePressure> = MutableStateFlow(
            ProductionReceivePressure(0, false, 0, 0),
        )
        override val state = MutableSharedFlow<RealtimeState>(replay = 1, extraBufferCapacity = 4)
        val connects = AtomicInteger(0)
        val disconnects = AtomicInteger(0)
        override fun connect() { connects.incrementAndGet() }
        override fun send(envelope: Envelope) = true
        override fun disconnect(reason: String) { disconnects.incrementAndGet() }
        override fun diagnostics() = ProductionRealtimeSocketDiagnostics(1, 1, true, "CONNECTED", 0, 0)
        override fun close() = Unit
    }

    private class FixedSecureRandom : SecureRandom() {
        override fun nextBytes(bytes: ByteArray) {
            bytes.indices.forEach { bytes[it] = (it + 1).toByte() }
        }
    }

    private fun TransportSignalPayloadV1.type(): TransportSignalTypeV1 = when {
        hasCutoverProposal() -> TYPE_PROPOSAL
        hasCutoverAccept() -> TYPE_ACCEPT
        hasCutoverCommit() -> TYPE_COMMIT
        hasFallback() -> TYPE_FALLBACK
        hasPathClose() -> TYPE_CLOSE
        else -> TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_UNSPECIFIED
    }

    private fun DirectNativeDirectionV5.toRealtime() = when (this) {
        DirectNativeDirectionV5.PC_TO_ANDROID -> Direction.PC_TO_ANDROID
        DirectNativeDirectionV5.ANDROID_TO_PC -> Direction.ANDROID_TO_PC
    }

    private fun fence(path: DirectNativePathV5) = ProductionDirectPathFence(
        path.epoch,
        path.connectionId.toUuid(),
        path.attemptId.toUuid(),
    )

    private fun defaultPath() = path(PATH_EPOCH, 10, 11)
    private fun path(epoch: Long, connectionSeed: Int, attemptSeed: Int) =
        DirectNativePathV5(epoch, uuidBytes(uuid(connectionSeed)), uuidBytes(uuid(attemptSeed)))

    @Suppress("UNCHECKED_CAST")
    private fun <T> field(target: Any, name: String): T = target.javaClass.getDeclaredField(name).run {
        isAccessible = true
        get(target) as T
    }

    private fun assertAllZero(value: ByteArray) = assertTrue(value.all { it == 0.toByte() })
    private fun ByteArray.toUuid(): UUID = ByteBuffer.wrap(this).let { UUID(it.long, it.long) }

    private companion object {
        val LOCAL: UUID = UUID(0, 1)
        val PEER: UUID = UUID(0, 2)
        const val PATH_EPOCH = 7L
        const val KEY_EPOCH = 9L
        const val SIGNAL_GENERATION = 6
        const val RELAY_GENERATION = 5
        const val NOW = 9_000_000L
        const val DATA_CAPABILITY = "direct_transport_data_v1"
        const val SESSION_CUTOVER_CAPABILITY = "direct_session_cutover_v1"
        val KEY_ID = byteArrayOf(5, 6, 7)
        val PAIRWISE_KEY = ByteArray(32) { (it + 1).toByte() }
        val DIRECT_PATH_BINDING = ByteArray(32) { (it + 33).toByte() }
        val TYPE_PROPOSAL = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_PROPOSAL
        val TYPE_ACCEPT = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_ACCEPT
        val TYPE_COMMIT = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_COMMIT
        val TYPE_FALLBACK = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_FALLBACK
        val TYPE_CLOSE = TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_CLOSE

        fun uuid(seed: Int) = UUID(0, seed.toLong())
        fun uuidBytes(value: UUID) = ByteBuffer.allocate(16)
            .putLong(value.mostSignificantBits)
            .putLong(value.leastSignificantBits)
            .array()
        fun capabilities(generation: Int = SIGNAL_GENERATION) =
            DirectTransportSignalingNegotiatedCapabilities(
                generation,
                setOf(DATA_CAPABILITY, SESSION_CUTOVER_CAPABILITY),
            )
    }
}

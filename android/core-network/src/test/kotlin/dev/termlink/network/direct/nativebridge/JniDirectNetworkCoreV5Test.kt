package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.ArrayDeque
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class JniDirectNetworkCoreV5Test {
    @Test
    fun malformedInputsAndStalePackagedAbiFailClosedBeforeCreate() {
        assertFailsWith<IllegalArgumentException> {
            DirectNativePathV5(0, ByteArray(16) { 1 }, ByteArray(16) { 2 })
        }
        assertFailsWith<IllegalArgumentException> {
            DirectNativePathV5(1, ByteArray(16), ByteArray(16) { 2 })
        }
        assertFailsWith<IllegalArgumentException> {
            DirectNativeCreateConfigV5(queueCapacityFrames = 257)
        }
        assertFailure(
            JniDirectNetworkCoreV5 { error("missing") }.create(),
            DirectNativeSyntheticReasonV5.NATIVE_UNAVAILABLE,
        )
        compatibleAbi().indices.forEach { index ->
            val incompatible = FakeV5Methods(
                abi = compatibleAbi().also {
                    it[index] = if (it[index] == Long.MAX_VALUE) it[index] - 1 else it[index] + 1
                },
            )
            assertFailure(
                JniDirectNetworkCoreV5 { incompatible }.create(),
                DirectNativeSyntheticReasonV5.ABI_MISMATCH,
            )
            assertEquals(0, incompatible.createCalls, "ABI slot $index reached native create")
        }
    }

    @Test
    fun v5SurfaceContainsNoRelayRendererSessionReconnectOrApplicationAckOwner() {
        val names = DirectNativeMethodsV5::class.java.declaredMethods.map { it.name }.toSet()
        assertEquals(
            setOf(
                "abiContractV5", "createV5", "pathUsageV5", "attachV5", "promoteV5",
                "submitSignatureV5", "receiveSigningPayloadV5", "pathInfoV5",
                "receiveEnvelopeV5", "startProposalV5", "receiveProposalV5",
                "receiveAcceptV5", "receiveCommitV5", "commitEchoSentV5",
                "receiveFallbackV5", "tickV5", "updateAppliedCursorV5", "deregisterSessionV5",
                "routeEnvelopeV5", "pollRouteActionV5", "arbitrateIngressV5",
                "tickIngressV5", "pollEventV5", "cancelV5", "networkChangedV5",
                "destroyV5",
            ),
            names,
        )
        val forbidden = Regex("relay|renderer|reconnect|application.*ack", RegexOption.IGNORE_CASE)
        assertTrue(names.none(forbidden::containsMatchIn))
    }

    @Test
    fun androidPromotionRecordUsesSemanticOutboundDirectionNotQuicRole() {
        val responder = nomination(role = DirectNativeRoleV4.RESPONDER)
        val encoded = DirectNativePromoteRequestV5(path(), responder).encode()
        val buffer = encoded.buffer()
        assertEquals(5, buffer.getInt(0))
        assertEquals(544, buffer.getInt(4))
        assertEquals(DirectNativeRoleV4.RESPONDER.code, buffer.getInt(392))
        assertEquals(DirectNativeDirectionV5.ANDROID_TO_PC.code, buffer.getInt(512))
        assertContentEquals(path().connectionId, encoded.copyOfRange(24, 40))
        assertContentEquals(responder.localProofNonce, encoded.copyOfRange(472, 488))
    }

    @Test
    fun sessionDeregistrationKeepsFullFenceSupportsBothDirectionsAndFailsClosed() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        val sessionId = ByteArray(16) { 23 }
        DirectNativeDirectionV5.entries.forEach { direction ->
            assertIs<DirectNativeResultV5.Success<Unit>>(
                handle.deregisterSession(DirectNativeCursorV5(path, direction, sessionId, 0)),
            )
        }
        assertEquals(2, methods.deregisterRecords.size)
        methods.deregisterRecords.forEachIndexed { index, record ->
            assertContentEquals(path.encode(), record.copyOfRange(8, 72))
            assertEquals(DirectNativeDirectionV5.entries[index].code, record.buffer().getInt(72))
            assertContentEquals(sessionId, record.copyOfRange(80, 96))
            assertEquals(0L, record.buffer().getLong(96))
        }

        assertFailure(
            handle.deregisterSession(
                DirectNativeCursorV5(path, DirectNativeDirectionV5.PC_TO_ANDROID, sessionId, 1),
            ),
            DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
        )
        assertFailure(
            handle.deregisterSession(
                DirectNativeCursorV5(path(epoch = 6), DirectNativeDirectionV5.PC_TO_ANDROID, sessionId, 0),
            ),
            DirectNativeSyntheticReasonV5.STALE_OWNER,
        )
        assertEquals(2, methods.deregisterRecords.size)
        handle.close()
    }

    @Test
    fun receiveCommitAcceptsInboundInitialAndOutboundEchoWhileCommitEchoSentIsInboundOnly() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        val barrier = ByteArray(16) { 21 }
        val digest = ByteArray(32) { 22 }
        fun transition(direction: DirectNativeDirectionV5, isEcho: Boolean) =
            DirectNativeTransitionV5(
                path = path,
                direction = direction,
                barrierId = barrier,
                barrierSha256 = digest,
                accepted = false,
                isEcho = isEcho,
                nowMillis = 100,
                timeoutMillis = 0,
            )

        assertIs<DirectNativeResultV5.Success<Unit>>(
            handle.receiveCommit(transition(DirectNativeDirectionV5.PC_TO_ANDROID, isEcho = false)),
        )
        assertIs<DirectNativeResultV5.Success<Unit>>(
            handle.receiveCommit(transition(DirectNativeDirectionV5.ANDROID_TO_PC, isEcho = true)),
        )
        val inboundEchoFailure = assertIs<DirectNativeResultV5.Failure>(
            handle.receiveCommit(transition(DirectNativeDirectionV5.PC_TO_ANDROID, isEcho = true)),
        )
        assertEquals(DirectNativeStatusV5.INVALID_ARGUMENT, inboundEchoFailure.nativeStatus)
        val outboundInitialFailure = assertIs<DirectNativeResultV5.Failure>(
            handle.receiveCommit(transition(DirectNativeDirectionV5.ANDROID_TO_PC, isEcho = false)),
        )
        assertEquals(DirectNativeStatusV5.INVALID_ARGUMENT, outboundInitialFailure.nativeStatus)

        assertIs<DirectNativeResultV5.Success<Unit>>(
            handle.commitEchoSent(transition(DirectNativeDirectionV5.PC_TO_ANDROID, isEcho = true)),
        )
        val wrongEchoDirection = assertIs<DirectNativeResultV5.Failure>(
            handle.commitEchoSent(transition(DirectNativeDirectionV5.ANDROID_TO_PC, isEcho = true)),
        )
        assertEquals(DirectNativeStatusV5.INVALID_ARGUMENT, wrongEchoDirection.nativeStatus)

        assertEquals(2, methods.receiveCommitRecords.size)
        assertEquals(
            listOf(
                DirectNativeDirectionV5.PC_TO_ANDROID.code to 0,
                DirectNativeDirectionV5.ANDROID_TO_PC.code to 1,
            ),
            methods.receiveCommitRecords.map { it.buffer().getInt(72) to it.buffer().getInt(132) },
        )
        assertEquals(1, methods.commitEchoSentRecords.size)
        assertEquals(
            DirectNativeDirectionV5.PC_TO_ANDROID.code,
            methods.commitEchoSentRecords.single().buffer().getInt(72),
        )
        assertEquals(1, methods.commitEchoSentRecords.single().buffer().getInt(132))
        handle.close()
    }

    @Test
    fun proposalOwnedSecretsAndNativeAbiRecordAreZeroizedAfterTheirOwnershipEnds() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        val proposal = DirectNativeProposalV5(
            path = path,
            direction = DirectNativeDirectionV5.ANDROID_TO_PC,
            barrierId = ByteArray(16) { 21 },
            barrierSha256 = ByteArray(32) { 22 },
            directPathBinding = ByteArray(32) { 23 },
            duplicateGraceMillis = 250,
            nowMillis = 100,
            timeoutMillis = 2_000,
            sessions = listOf(
                DirectNativeSessionBarrierV5(ByteArray(16) { 24 }, 7),
            ),
        )

        assertIs<DirectNativeResultV5.Success<Unit>>(handle.startProposal(proposal))
        assertTrue(methods.lastStartProposalRecordReference!!.all { it == 0.toByte() })
        assertTrue(proposal.barrierId.any { it != 0.toByte() })
        assertTrue(proposal.directPathBinding.any { it != 0.toByte() })

        proposal.close()
        proposal.close()
        assertTrue(proposal.path.connectionId.all { it == 0.toByte() })
        assertTrue(proposal.path.attemptId.all { it == 0.toByte() })
        assertTrue(proposal.barrierId.all { it == 0.toByte() })
        assertTrue(proposal.barrierSha256.all { it == 0.toByte() })
        assertTrue(proposal.directPathBinding.all { it == 0.toByte() })
        assertTrue(proposal.sessions.single().sessionId.all { it == 0.toByte() })
        handle.close()
    }

    @Test
    fun signingPayloadUsesNonDestructiveProbeAndOnlyRequestedSignatureIsAccepted() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        methods.signing += FakeBytes(71, byteArrayOf(9, 8, 7))
        val payload = assertIs<DirectNativeResultV5.Success<DirectNativeSigningPayloadV5?>>(
            handle.receiveSigningPayload(path),
        ).value!!
        assertEquals(71, payload.requestId)
        assertContentEquals(byteArrayOf(9, 8, 7), payload.bytes)
        assertEquals(listOf(0, 3), methods.signingCapacities)

        assertFailure(
            handle.submitSignature(path, 72, ByteArray(64) { 4 }),
            DirectNativeSyntheticReasonV5.SIGNATURE_NOT_REQUESTED,
        )
        val signature = ByteArray(64) { 4 }
        assertIs<DirectNativeResultV5.Success<Unit>>(handle.submitSignature(path, 71, signature))
        signature.fill(99)
        assertContentEquals(ByteArray(64) { 4 }, methods.lastSignature!!.copyOfRange(88, 152))
        handle.close()
    }

    @Test
    fun incomingEnvelopeAndRouteActionReadsDoNotPopOnShortBuffer() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        methods.incoming += FakeBytes(4, byteArrayOf(1, 2, 3))
        val envelope = assertIs<DirectNativeResultV5.Success<DirectNativeEnvelopeV5?>>(
            handle.receiveEnvelope(path),
        ).value!!
        assertEquals(4, envelope.frameSequence)
        assertContentEquals(byteArrayOf(1, 2, 3), envelope.bytes)
        assertEquals(listOf(0, 3), methods.envelopeCapacities)

        methods.actions += FakeAction(relayAction(path, byteArrayOf(6, 7)), byteArrayOf(6, 7))
        val action = assertIs<DirectNativeResultV5.Success<DirectNativeRouteActionV5?>>(
            handle.pollRouteAction(path),
        ).value!!
        assertEquals(DirectNativeRouteActionKindV5.RELAY_ENVELOPE, action.kind)
        assertContentEquals(byteArrayOf(6, 7), action.payload)
        assertEquals(listOf(0, 2), methods.routeActionCapacities)
        assertNull(
            assertIs<DirectNativeResultV5.Success<DirectNativeRouteActionV5?>>(
                handle.pollRouteAction(path),
            ).value,
        )
        handle.close()
    }

    @Test
    fun metadataOnlyDirectHoldAndDropActionsPreserveLogicalEnvelopeSize() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.DIRECT_ENVELOPE,
                carrier = DirectNativeCarrierV5.LAN_DIRECT,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                directFrameSequence = 4,
                payloadBytes = 37,
                reason = 9,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.INGRESS_HOLD,
                carrier = DirectNativeCarrierV5.NAT_DIRECT,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                payloadBytes = 41,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.INGRESS_DROP,
                carrier = DirectNativeCarrierV5.RELAY,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                payloadBytes = 43,
            ),
            ByteArray(0),
        )

        val direct = successAction(handle.pollRouteAction(path))
        assertEquals(DirectNativeRouteActionKindV5.DIRECT_ENVELOPE, direct.kind)
        assertEquals(37, direct.payloadBytes)
        assertTrue(direct.payload.isEmpty())
        val hold = successAction(handle.pollRouteAction(path))
        assertEquals(DirectNativeRouteActionKindV5.INGRESS_HOLD, hold.kind)
        assertEquals(41, hold.payloadBytes)
        assertTrue(hold.payload.isEmpty())
        val drop = successAction(handle.pollRouteAction(path))
        assertEquals(DirectNativeRouteActionKindV5.INGRESS_DROP, drop.kind)
        assertEquals(43, drop.payloadBytes)
        assertTrue(drop.payload.isEmpty())
        assertEquals(listOf(0, 0, 0), methods.routeActionCapacities)
        handle.close()
    }

    @Test
    fun routeActionDecoderAcceptsFrozenFallbackSequenceDomain() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)

        listOf(0L, 3L, 4L, Long.MAX_VALUE).forEach { sequence ->
            methods.actions += FakeAction(
                controlAction(
                    path = path,
                    kind = DirectNativeRouteActionKindV5.FALLBACK,
                    direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                    directFrameSequence = sequence,
                    reason = 1,
                ),
                ByteArray(0),
            )
            assertEquals(
                sequence,
                successAction(handle.pollRouteAction(path)).directFrameSequence,
            )
        }
        handle.close()
    }

    @Test
    fun routeActionDecoderAcceptsEveryFrozenV5ActionShape() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        val barrier = ByteArray(16) { 21 }
        val digest = ByteArray(32) { 22 }
        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.ACCEPT,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                barrierId = barrier,
                barrierSha256 = digest,
                accepted = true,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.COMMIT,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                barrierId = barrier,
                barrierSha256 = digest,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.FALLBACK,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                barrierId = barrier,
                // Native starts Direct transport sequencing at 4, so a
                // fallback before any received data reports the exact cursor 3.
                directFrameSequence = 3,
                reason = 1,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.CARRIER,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                carrier = DirectNativeCarrierV5.NAT_DIRECT,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.SESSION_CARRIER,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                sessionId = ByteArray(16) { 23 },
                carrier = DirectNativeCarrierV5.RELAY,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.INGRESS_DELIVER,
                carrier = DirectNativeCarrierV5.LAN_DIRECT,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                payloadBytes = 3,
            ),
            byteArrayOf(4, 5, 6),
        )

        assertEquals(DirectNativeRouteActionKindV5.ACCEPT, successAction(handle.pollRouteAction(path)).kind)
        assertEquals(DirectNativeRouteActionKindV5.COMMIT, successAction(handle.pollRouteAction(path)).kind)
        assertEquals(DirectNativeRouteActionKindV5.FALLBACK, successAction(handle.pollRouteAction(path)).kind)
        assertEquals(DirectNativeRouteActionKindV5.CARRIER, successAction(handle.pollRouteAction(path)).kind)
        assertEquals(DirectNativeRouteActionKindV5.SESSION_CARRIER, successAction(handle.pollRouteAction(path)).kind)
        val deliver = successAction(handle.pollRouteAction(path))
        assertEquals(DirectNativeRouteActionKindV5.INGRESS_DELIVER, deliver.kind)
        assertContentEquals(byteArrayOf(4, 5, 6), deliver.payload)
        handle.close()
    }

    @Test
    fun routeActionDecoderRejectsPayloadAndFieldShapeConfusion() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.DIRECT_ENVELOPE,
                carrier = DirectNativeCarrierV5.LAN_DIRECT,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                directFrameSequence = 4,
                payloadBytes = 2,
            ),
            byteArrayOf(1, 2),
        )
        assertFailure(
            handle.pollRouteAction(path),
            DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE,
        )

        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.INGRESS_DELIVER,
                carrier = DirectNativeCarrierV5.RELAY,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                payloadBytes = 2,
            ).also { it.buffer().putInt(172, 1) },
            byteArrayOf(1, 2),
        )
        assertFailure(
            handle.pollRouteAction(path),
            DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE,
        )

        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.CARRIER,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                carrier = DirectNativeCarrierV5.RELAY,
            ).also { it.buffer().putInt(168, 99) },
            ByteArray(0),
        )
        assertFailure(handle.pollRouteAction(path), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)

        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.DIRECT_ENVELOPE,
                carrier = DirectNativeCarrierV5.NAT_DIRECT,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                directFrameSequence = 4,
                payloadBytes = 2,
                reason = 10,
            ),
            ByteArray(0),
        )
        assertFailure(handle.pollRouteAction(path), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)

        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.FALLBACK,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                directFrameSequence = 3,
                reason = 10,
            ),
            ByteArray(0),
        )
        assertFailure(handle.pollRouteAction(path), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)

        for (invalidFallbackSequence in listOf(1L, 2L, -1L, Long.MIN_VALUE)) {
            methods.actions += FakeAction(
                controlAction(
                    path = path,
                    kind = DirectNativeRouteActionKindV5.FALLBACK,
                    direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                    directFrameSequence = invalidFallbackSequence,
                    reason = 1,
                ),
                ByteArray(0),
            )
            assertFailure(
                handle.pollRouteAction(path),
                DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE,
            )
        }

        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.ACCEPT,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                barrierId = ByteArray(16) { 2 },
                barrierSha256 = ByteArray(32) { 3 },
                accepted = false,
                reason = 0,
            ),
            ByteArray(0),
        )
        assertFailure(handle.pollRouteAction(path), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)

        methods.actions += FakeAction(
            envelopeAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.INGRESS_DELIVER,
                carrier = DirectNativeCarrierV5.RELAY,
                direction = DirectNativeDirectionV5.PC_TO_ANDROID,
                payloadBytes = 2,
            ).also { ByteArray(16).copyInto(it, destinationOffset = 80) },
            byteArrayOf(1, 2),
        )
        assertFailure(handle.pollRouteAction(path), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)

        methods.actions += FakeAction(
            controlAction(
                path = path,
                kind = DirectNativeRouteActionKindV5.CARRIER,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                carrier = DirectNativeCarrierV5.RELAY,
            ).also { it.buffer().putLong(96, 1) },
            ByteArray(0),
        )
        assertFailure(handle.pollRouteAction(path), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)
        handle.close()
    }

    @Test
    fun routeAndIngressCopyExactEnvelopeAndRemainFencedToOnePath() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        val session = ByteArray(16) { 7 }
        val route = DirectNativeRouteEnvelopeV5(
            path, DirectNativeDirectionV5.ANDROID_TO_PC, true, session, 9, 100,
        )
        val bytes = byteArrayOf(4, 5, 6)
        assertIs<DirectNativeResultV5.Success<Unit>>(handle.routeEnvelope(route, bytes))
        bytes.fill(99)
        assertContentEquals(byteArrayOf(4, 5, 6), methods.lastRouteEnvelope)

        val ingress = DirectNativeIngressV5(
            path, DirectNativeDirectionV5.PC_TO_ANDROID, DirectNativeIngressSourceV5.DIRECT,
            session, 10, 101, 250,
        )
        assertIs<DirectNativeResultV5.Success<Unit>>(
            handle.arbitrateIngress(ingress, byteArrayOf(8, 9)),
        )
        assertContentEquals(byteArrayOf(8, 9), methods.lastIngressEnvelope)
        assertFailure(
            handle.tickIngress(path(epoch = 6), 102),
            DirectNativeSyntheticReasonV5.STALE_OWNER,
        )
        assertEquals(0, methods.tickIngressCalls)

        val wrongRoute = DirectNativeRouteEnvelopeV5(
            path, DirectNativeDirectionV5.PC_TO_ANDROID, false, session, 11, 102,
        )
        val wrongRouteFailure = assertIs<DirectNativeResultV5.Failure>(
            handle.routeEnvelope(wrongRoute, byteArrayOf(1)),
        )
        assertEquals(DirectNativeStatusV5.INVALID_ARGUMENT, wrongRouteFailure.nativeStatus)
        val wrongIngress = DirectNativeIngressV5(
            path, DirectNativeDirectionV5.ANDROID_TO_PC, DirectNativeIngressSourceV5.RELAY,
            session, 12, 103, 0,
        )
        val wrongIngressFailure = assertIs<DirectNativeResultV5.Failure>(
            handle.arbitrateIngress(wrongIngress, byteArrayOf(2)),
        )
        assertEquals(DirectNativeStatusV5.INVALID_ARGUMENT, wrongIngressFailure.nativeStatus)
        handle.close()
    }

    @Test
    fun successorPathDrainsPredecessorExactRelayRecoveryWithoutAuthorizingOldDirectWork() {
        val methods = FakeV5Methods()
        val predecessor = path(epoch = 7)
        val successor = path(epoch = 8)
        val handle = createdHandle(methods, predecessor)
        assertIs<DirectNativeResultV5.Success<Unit>>(handle.usePath(successor))

        methods.actions += FakeAction(
            envelopeAction(
                path = predecessor,
                kind = DirectNativeRouteActionKindV5.DIRECT_ENVELOPE,
                carrier = DirectNativeCarrierV5.LAN_DIRECT,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                directFrameSequence = 4,
                payloadBytes = 3,
            ),
            ByteArray(0),
        )
        methods.actions += FakeAction(
            envelopeAction(
                path = predecessor,
                kind = DirectNativeRouteActionKindV5.RELAY_ENVELOPE,
                carrier = DirectNativeCarrierV5.RELAY,
                direction = DirectNativeDirectionV5.ANDROID_TO_PC,
                payloadBytes = 3,
            ).also { it.buffer().putInt(176, 1) },
            byteArrayOf(7, 8, 9),
        )

        val fallback = successAction(handle.pollRouteAction(successor))
        assertTrue(fallback.path.sameFence(predecessor))
        assertEquals(DirectNativeRouteActionKindV5.RELAY_ENVELOPE, fallback.kind)
        assertContentEquals(byteArrayOf(7, 8, 9), fallback.payload)
        assertNull(
            assertIs<DirectNativeResultV5.Success<DirectNativeRouteActionV5?>>(
                handle.pollRouteAction(successor),
            ).value,
        )
        assertFailure(
            handle.pollRouteAction(predecessor),
            DirectNativeSyntheticReasonV5.STALE_OWNER,
        )
        handle.close()
    }

    @Test
    fun staleEventCannotOperateCurrentPathAndCloseIsIdempotent() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        methods.events += event(path, kind = 8, flags = 8)
        assertEquals(
            DirectNativeEventKindV5.PATH_READY,
            assertIs<DirectNativeResultV5.Success<DirectNativeEventV5?>>(handle.pollEvent())
                .value?.kind,
        )
        methods.events += event(path(epoch = 6), kind = 9, status = DirectNativeStatusV5.PROTOCOL)
        assertFailure(handle.pollEvent(), DirectNativeSyntheticReasonV5.STALE_OWNER)
        handle.close()
        handle.close()
        assertEquals(1, methods.destroyCalls)
    }

    @Test
    fun eventDecoderEnforcesExactKindSpecificStatusFlagsAndCounters() {
        val methods = FakeV5Methods()
        val path = path()
        val handle = createdHandle(methods, path)
        val validEvents = listOf(
            DirectNativeEventKindV5.PATH_USED to event(path, DirectNativeEventKindV5.PATH_USED.code),
            DirectNativeEventKindV5.ENVELOPE_QUEUED to event(
                path, DirectNativeEventKindV5.ENVELOPE_QUEUED.code,
                flags = 1 or 4, frameSequence = 4, payloadBytes = 3,
            ),
            DirectNativeEventKindV5.ENVELOPE_AVAILABLE to event(
                path, DirectNativeEventKindV5.ENVELOPE_AVAILABLE.code,
                flags = 2, frameSequence = 5, payloadBytes = 4,
            ),
            DirectNativeEventKindV5.TRANSPORT_RECEIPT to event(
                path, DirectNativeEventKindV5.TRANSPORT_RECEIPT.code,
                flags = 1, frameSequence = 6,
            ),
            DirectNativeEventKindV5.TRANSPORT_RECEIPT to event(
                path, DirectNativeEventKindV5.TRANSPORT_RECEIPT.code,
                flags = 2 or 4, frameSequence = 7, releasedFrames = 2,
            ),
            DirectNativeEventKindV5.CANCELLED to event(path, DirectNativeEventKindV5.CANCELLED.code),
            DirectNativeEventKindV5.NETWORK_CHANGED to event(
                path, DirectNativeEventKindV5.NETWORK_CHANGED.code,
            ),
            DirectNativeEventKindV5.QUEUE_LIMIT to event(
                path, DirectNativeEventKindV5.QUEUE_LIMIT.code,
                status = DirectNativeStatusV5.QUEUE_FULL,
            ),
            DirectNativeEventKindV5.PATH_READY to event(
                path, DirectNativeEventKindV5.PATH_READY.code, flags = 8,
            ),
            DirectNativeEventKindV5.DIRECT_FAILED to event(
                path, DirectNativeEventKindV5.DIRECT_FAILED.code,
                status = DirectNativeStatusV5.PROTOCOL,
            ),
            DirectNativeEventKindV5.PING to event(
                path, DirectNativeEventKindV5.PING.code, flags = 1, frameSequence = 8,
            ),
            DirectNativeEventKindV5.PONG to event(
                path, DirectNativeEventKindV5.PONG.code, flags = 2, frameSequence = 9,
            ),
            // SIGN_REQUEST.frame_seq is its request ID, not a data-frame sequence.
            DirectNativeEventKindV5.SIGN_REQUEST to event(
                path, DirectNativeEventKindV5.SIGN_REQUEST.code,
                frameSequence = 1, payloadBytes = 17,
            ),
        )
        validEvents.forEach { (expectedKind, bytes) ->
            methods.events += bytes
            val decoded = assertIs<DirectNativeResultV5.Success<DirectNativeEventV5?>>(
                handle.pollEvent(),
            ).value!!
            assertEquals(expectedKind, decoded.kind)
        }

        val malformedEvents = listOf(
            event(path, DirectNativeEventKindV5.PATH_READY.code, flags = 8 or 16),
            event(
                path, DirectNativeEventKindV5.DIRECT_FAILED.code,
                status = DirectNativeStatusV5.OK,
            ),
            event(
                path, DirectNativeEventKindV5.DIRECT_FAILED.code,
                status = DirectNativeStatusV5.TICKET_EXPIRED - 1,
            ),
            event(
                path, DirectNativeEventKindV5.TRANSPORT_RECEIPT.code,
                flags = 1, frameSequence = 4, releasedFrames = 1,
            ),
            event(
                path, DirectNativeEventKindV5.TRANSPORT_RECEIPT.code,
                flags = 2, frameSequence = 4,
            ),
            event(
                path, DirectNativeEventKindV5.ENVELOPE_AVAILABLE.code,
                flags = 2, frameSequence = 4, payloadBytes = 1, releasedFrames = 1,
            ),
            event(
                path, DirectNativeEventKindV5.SIGN_REQUEST.code,
                frameSequence = 0, payloadBytes = 17,
            ),
            event(path, DirectNativeEventKindV5.QUEUE_LIMIT.code),
        )
        malformedEvents.forEach { bytes ->
            methods.events += bytes
            assertFailure(handle.pollEvent(), DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE)
        }
        handle.close()
    }

    @Test
    fun pathInfoAllowsPreReadyZeroMetadataButRequiresAuthenticatedReadyBinding() {
        val path = path()
        val preReady = pathInfo(path, kind = 0, ready = false, binding = ByteArray(32))
        val decodedPreReady = decodePathInfoV5(preReady)!!
        assertNull(decodedPreReady.pathKind)
        assertFalse(decodedPreReady.ready)

        val ready = pathInfo(path, kind = 1, ready = true, binding = ByteArray(32) { 3 })
        val decodedReady = decodePathInfoV5(ready)!!
        assertEquals(DirectNativePathKindV5.LAN, decodedReady.pathKind)
        assertTrue(decodedReady.ready)
        assertContentEquals(ByteArray(32) { 3 }, decodedReady.directPathBinding)

        assertNull(
            decodePathInfoV5(
                pathInfo(path, kind = 1, ready = false, binding = ByteArray(32)),
            ),
        )
        assertNull(
            decodePathInfoV5(
                pathInfo(path, kind = 0, ready = false, binding = ByteArray(32) { 3 }),
            ),
        )
        assertNull(decodePathInfoV5(pathInfo(path, kind = 1, ready = true, binding = ByteArray(32))))
    }

    private fun createdHandle(methods: FakeV5Methods, path: DirectNativePathV5) =
        assertIs<DirectNativeResultV5.Success<DirectNativeHandleV5>>(
            JniDirectNetworkCoreV5 { methods }.create(
                DirectNativeCreateConfigV5(enabled = true, initialPath = path),
            ),
        ).value

    private fun path(epoch: Long = 7) = DirectNativePathV5(
        epoch,
        ByteArray(16) { (epoch + 1).toByte() },
        ByteArray(16) { (epoch + 2).toByte() },
    )

    private fun candidate(id: Int) = DirectNativeCandidateV4(
        ByteArray(16) { id.toByte() },
        ByteArray(16),
        DirectNativeSocketAddressV4(byteArrayOf(10, 0, 0, id.toByte()), 50_000 + id),
        DirectNativeCandidateKindV4.HOST,
        2_130_706_431,
        10,
        9_000_000,
    )

    private fun nomination(role: DirectNativeRoleV4) = DirectNativeCommitNominationRequestV4(
        epoch = 7,
        attemptId = ByteArray(16) { 9 },
        checkRoundId = ByteArray(16) { 11 },
        nominationId = ByteArray(16) { 12 },
        winningPair = DirectNativeCandidatePairV4(1, candidate(1), candidate(2)),
        role = role,
        timeoutMillis = 2_000,
        selectionConfirmed = true,
        peerCertificateSha256 = ByteArray(32) { 13 },
        peerDeviceSigningPublicKey = ByteArray(32) { 14 },
        localProofNonce = ByteArray(16) { 15 },
        nowMillis = 100,
    )

    private fun relayAction(path: DirectNativePathV5, payload: ByteArray): ByteArray =
        ByteBuffer.allocate(200).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(0, 5)
            putInt(4, 200)
            putBytes(8, path.encode())
            putInt(72, 2)
            putInt(76, 2)
            putBytes(80, ByteArray(16) { 7 })
            putLong(96, 9)
            putInt(168, DirectNativeCarrierV5.RELAY.code)
            putInt(180, payload.size)
        }.array()

    private fun envelopeAction(
        path: DirectNativePathV5,
        kind: DirectNativeRouteActionKindV5,
        carrier: DirectNativeCarrierV5,
        direction: DirectNativeDirectionV5,
        directFrameSequence: Long = 0,
        payloadBytes: Int,
        reason: Int = 0,
    ): ByteArray = ByteBuffer.allocate(200).order(ByteOrder.LITTLE_ENDIAN).apply {
        putInt(0, 5)
        putInt(4, 200)
        putBytes(8, path.encode())
        putInt(72, kind.code)
        putInt(76, direction.code)
        putBytes(80, ByteArray(16) { 7 })
        putLong(96, 9)
        putLong(104, directFrameSequence)
        putInt(168, carrier.code)
        putInt(176, reason)
        putInt(180, payloadBytes)
    }.array()

    private fun controlAction(
        path: DirectNativePathV5,
        kind: DirectNativeRouteActionKindV5,
        direction: DirectNativeDirectionV5,
        barrierId: ByteArray = ByteArray(16),
        barrierSha256: ByteArray = ByteArray(32),
        directFrameSequence: Long = 0,
        accepted: Boolean = false,
        isEcho: Boolean = false,
        sessionId: ByteArray = ByteArray(16),
        carrier: DirectNativeCarrierV5? = null,
        reason: Int = 0,
    ): ByteArray = ByteBuffer.allocate(200).order(ByteOrder.LITTLE_ENDIAN).apply {
        putInt(0, 5)
        putInt(4, 200)
        putBytes(8, path.encode())
        putInt(72, kind.code)
        putInt(76, direction.code)
        putBytes(80, sessionId)
        putLong(104, directFrameSequence)
        putBytes(112, barrierId)
        putBytes(128, barrierSha256)
        putInt(160, if (accepted) 1 else 0)
        putInt(164, if (isEcho) 1 else 0)
        putInt(168, carrier?.code ?: 0)
        putInt(176, reason)
    }.array()

    private fun pathInfo(
        path: DirectNativePathV5,
        kind: Int,
        ready: Boolean,
        binding: ByteArray,
    ): ByteArray = ByteBuffer.allocate(128).order(ByteOrder.LITTLE_ENDIAN).apply {
        putInt(0, 5)
        putInt(4, 128)
        putBytes(8, path.encode())
        putInt(72, kind)
        putInt(76, if (ready) 1 else 0)
        putBytes(80, binding)
    }.array()

    private fun event(
        path: DirectNativePathV5,
        kind: Int,
        flags: Int = 0,
        status: Int = DirectNativeStatusV5.OK,
        frameSequence: Long = 0,
        payloadBytes: Int = 0,
        releasedFrames: Int = 0,
    ): ByteArray =
        ByteBuffer.allocate(80).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(0, 5)
            putInt(4, 80)
            putInt(8, kind)
            putInt(12, status)
            putInt(16, flags)
            putLong(24, path.epoch)
            putBytes(32, path.attemptId)
            putLong(48, frameSequence)
            putInt(56, payloadBytes)
            putInt(60, releasedFrames)
        }.array()

    private fun successAction(value: DirectNativeResultV5<DirectNativeRouteActionV5?>) =
        assertIs<DirectNativeResultV5.Success<DirectNativeRouteActionV5?>>(value).value!!

    private fun assertFailure(value: DirectNativeResultV5<*>, reason: Int) {
        assertEquals(reason, assertIs<DirectNativeResultV5.Failure>(value).reason)
    }

    private fun compatibleAbi() = longArrayOf(
        5,
        256, 8L * 1024L * 1024L, 1024L * 1024L - 1L,
        4, 260, 4, Long.MAX_VALUE, 256,
        64, 112, 80, 48, 128, 288, 544, 128, 232, 24, 968, 176, 120, 128, 136, 200,
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    )

    private data class FakeBytes(val first: Long, val bytes: ByteArray)
    private data class FakeAction(val record: ByteArray, val payload: ByteArray)

    private class FakeV5Methods(
        private val abi: LongArray = longArrayOf(
            5,
            256, 8L * 1024L * 1024L, 1024L * 1024L - 1L,
            4, 260, 4, Long.MAX_VALUE, 256,
            64, 112, 80, 48, 128, 288, 544, 128, 232, 24, 968, 176, 120, 128, 136, 200,
            8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        ),
    ) : DirectNativeMethodsV5 {
        var createCalls = 0
        var destroyCalls = 0
        var tickIngressCalls = 0
        var lastSignature: ByteArray? = null
        var lastRouteEnvelope: ByteArray? = null
        var lastIngressEnvelope: ByteArray? = null
        var lastStartProposalRecordReference: ByteArray? = null
        val signing = ArrayDeque<FakeBytes>()
        val incoming = ArrayDeque<FakeBytes>()
        val actions = ArrayDeque<FakeAction>()
        val events = ArrayDeque<ByteArray>()
        val signingCapacities = mutableListOf<Int>()
        val envelopeCapacities = mutableListOf<Int>()
        val routeActionCapacities = mutableListOf<Int>()
        val receiveCommitRecords = mutableListOf<ByteArray>()
        val commitEchoSentRecords = mutableListOf<ByteArray>()
        val deregisterRecords = mutableListOf<ByteArray>()

        override fun abiContractV5() = abi.copyOf()
        override fun createV5(
            enabled: Boolean,
            queueCapacityFrames: Int,
            queueCapacityBytes: Long,
            path: ByteArray,
            outHandle: LongArray,
        ): Int {
            createCalls++
            outHandle[0] = 51
            return 0
        }
        override fun pathUsageV5(handle: Long, path: ByteArray) = 0
        override fun attachV5(handle: Long, path: ByteArray) = 0
        override fun promoteV5(handle: Long, v4Handle: Long, promote: ByteArray) = 0
        override fun submitSignatureV5(handle: Long, signature: ByteArray): Int {
            lastSignature = signature.copyOf()
            return 0
        }
        override fun receiveSigningPayloadV5(
            handle: Long,
            path: ByteArray,
            metadata: LongArray,
            output: ByteArray,
        ): Int {
            signingCapacities += output.size
            val value = signing.peekFirst() ?: return 1
            metadata[1] = value.bytes.size.toLong()
            if (output.size < value.bytes.size) return -5
            metadata[0] = value.first
            value.bytes.copyInto(output)
            signing.removeFirst()
            return 0
        }
        override fun pathInfoV5(handle: Long, path: ByteArray, output: ByteArray) = -6
        override fun receiveEnvelopeV5(
            handle: Long,
            path: ByteArray,
            metadata: LongArray,
            output: ByteArray,
        ): Int {
            envelopeCapacities += output.size
            val value = incoming.peekFirst() ?: return 1
            metadata[1] = value.bytes.size.toLong()
            if (output.size < value.bytes.size) return -5
            metadata[0] = value.first
            value.bytes.copyInto(output)
            incoming.removeFirst()
            return 0
        }
        override fun startProposalV5(handle: Long, proposal: ByteArray): Int {
            lastStartProposalRecordReference = proposal
            return 0
        }
        override fun receiveProposalV5(handle: Long, proposal: ByteArray) = 0
        override fun receiveAcceptV5(handle: Long, transition: ByteArray) = 0
        override fun receiveCommitV5(handle: Long, transition: ByteArray): Int {
            receiveCommitRecords += transition.copyOf()
            return 0
        }
        override fun commitEchoSentV5(handle: Long, transition: ByteArray): Int {
            commitEchoSentRecords += transition.copyOf()
            return 0
        }
        override fun receiveFallbackV5(handle: Long, transition: ByteArray) = 0
        override fun tickV5(handle: Long, transition: ByteArray) = 0
        override fun updateAppliedCursorV5(handle: Long, cursor: ByteArray) = 0
        override fun deregisterSessionV5(handle: Long, session: ByteArray): Int {
            deregisterRecords += session.copyOf()
            return 0
        }
        override fun routeEnvelopeV5(handle: Long, route: ByteArray, envelope: ByteArray): Int {
            // The native boundary copies the exact Envelope before returning;
            // mirror that ownership contract instead of retaining the bridge's
            // zeroized temporary buffer.
            lastRouteEnvelope = envelope.copyOf()
            return 0
        }
        override fun pollRouteActionV5(
            handle: Long,
            action: ByteArray,
            output: ByteArray,
            requiredBytes: LongArray,
        ): Int {
            routeActionCapacities += output.size
            val value = actions.peekFirst() ?: return 1
            requiredBytes[0] = value.payload.size.toLong()
            if (output.size < value.payload.size) return -5
            value.record.copyInto(action)
            value.payload.copyInto(output)
            actions.removeFirst()
            return 0
        }
        override fun arbitrateIngressV5(
            handle: Long,
            ingress: ByteArray,
            envelope: ByteArray,
        ): Int {
            lastIngressEnvelope = envelope.copyOf()
            return 0
        }
        override fun tickIngressV5(handle: Long, path: ByteArray, nowMillis: Long): Int {
            tickIngressCalls++
            return 0
        }
        override fun pollEventV5(handle: Long, output: ByteArray): Int {
            val value = events.pollFirst() ?: return 0
            value.copyInto(output)
            return 1
        }
        override fun cancelV5(handle: Long, path: ByteArray) = 0
        override fun networkChangedV5(handle: Long, path: ByteArray) = 0
        override fun destroyV5(handle: Long): Int {
            destroyCalls++
            return 0
        }
    }
}

private fun ByteArray.buffer() = ByteBuffer.wrap(this).order(ByteOrder.LITTLE_ENDIAN)

private fun ByteBuffer.putBytes(offset: Int, value: ByteArray) {
    val previous = position()
    position(offset)
    put(value)
    position(previous)
}

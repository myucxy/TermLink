package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.ArrayDeque
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertTrue

class JniDirectNetworkCoreV4Test {
    @Test
    fun malformedV4InputsAreRejectedBeforeJni() {
        assertFailsWith<IllegalArgumentException> {
            DirectNativeSocketAddressV4(byteArrayOf(1, 2, 3), 50_000)
        }
        assertFailsWith<IllegalArgumentException> {
            DirectNativeCandidateV4(
                ByteArray(16), ByteArray(16),
                DirectNativeSocketAddressV4(byteArrayOf(10, 0, 0, 2), 50_000),
                DirectNativeCandidateKindV4.HOST, 1, 0, 1,
            )
        }
        assertFailsWith<IllegalArgumentException> {
            begin(listOf(pair(2)), total = 1, complete = true)
        }
        assertFailsWith<IllegalArgumentException> {
            DirectNativeCommitNominationRequestV4(
                epoch = 7, attemptId = ByteArray(16) { 7 },
                checkRoundId = ByteArray(16) { 14 }, nominationId = ByteArray(16) { 18 },
                winningPair = pair(1), role = DirectNativeRoleV4.INITIATOR,
                timeoutMillis = 2_000, selectionConfirmed = false,
                peerCertificateSha256 = ByteArray(32) { 1 },
                peerDeviceSigningPublicKey = ByteArray(32) { 2 },
                localProofNonce = ByteArray(16) { 3 }, nowMillis = 10,
            )
        }
    }

    @Test
    fun missingOrMismatchedV4SymbolsFailClosedWithoutTryingV3() {
        assertFailure(
            JniDirectNetworkCoreV4 { error("missing v4 symbols") }.create(),
            DirectNativeSyntheticReasonV4.NATIVE_UNAVAILABLE,
        )
        val incompatible = FakeV4Methods(abi = compatibleAbi().also { it[7] = 1 })
        assertFailure(
            JniDirectNetworkCoreV4 { incompatible }.create(),
            DirectNativeSyntheticReasonV4.ABI_MISMATCH,
        )
        assertEquals(0, incompatible.createCalls)
    }

    @Test
    fun v4AbiSurfaceHasNoRelaySessionAckReconnectOrRoutingApi() {
        assertEquals(
            setOf(
                "abiContractV4", "createV4", "gatherV4", "beginChecksV4",
                "appendCheckPairsV4", "commitNominationV4", "cancelV4",
                "networkChangedV4", "submitSignatureV4", "pollV4", "destroyV4",
            ),
            DirectNativeMethodsV4::class.java.declaredMethods.map { it.name }.toSet(),
        )
        val forbidden = Regex("relay|session|envelope|ack|reconnect|routing", RegexOption.IGNORE_CASE)
        assertTrue(DirectNativeMethodsV4::class.java.declaredMethods.none { forbidden.containsMatchIn(it.name) })
        assertFalse(JniDirectNetworkCoreV4 { FakeV4Methods() }.directRoutingPermitted)
    }

    @Test
    fun gatherCopiesInputsAndEpochAttemptControlsAreExact() {
        val methods = FakeV4Methods()
        val handle = createdHandle(methods)
        val attempt = ByteArray(16) { 7 }
        val ip = byteArrayOf(10, 0, 0, 2)
        val request = gather(attemptId = attempt, bindIp = ip)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.gather(request))
        attempt.fill(99)
        ip.fill(99)
        assertContentEquals(ByteArray(16) { 7 }, methods.lastGatherBlobs!![0])
        assertContentEquals(byteArrayOf(10, 0, 0, 2), methods.lastGatherBlobs!![1])
        assertFailure(handle.cancel(6, ByteArray(16) { 7 }), DirectNativeSyntheticReasonV4.STALE_OWNER)
        assertFailure(handle.cancel(7, ByteArray(16) { 6 }), DirectNativeSyntheticReasonV4.STALE_OWNER)
        assertEquals(0, methods.cancelCalls)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.cancel(7, ByteArray(16) { 7 }))
        assertEquals(1, methods.cancelCalls)
        assertEquals(1, methods.destroyCalls)
        assertFalse(handle.directRoutingPermitted)
    }

    @Test
    fun candidateBatchBeginAppendValidateAndCommitUseOneFencedPlan() {
        val methods = FakeV4Methods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.gather(gather()))
        val first = pair(1)
        val begin = begin(pairs = listOf(first), total = 1, complete = false)
        assertFailure(handle.beginChecks(begin), DirectNativeSyntheticReasonV4.INVALID_PLAN)

        methods.events += candidateBatchEvent(batch = 1, count = 1, total = 1, complete = false)
        assertEquals(
            DirectNativeEventKindV4.CANDIDATE_BATCH,
            assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll()).value?.kind,
        )
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.beginChecks(begin))
        assertEquals(1, methods.beginCalls)
        assertContentEquals(begin.ticket, methods.lastBeginBlobs!![4])
        assertTrue(methods.lastBeginBlobs!![4] !== begin.ticket)

        assertFailure(
            handle.appendCheckPairs(append(batch = 3, pair = pair(2))),
            DirectNativeSyntheticReasonV4.INVALID_PLAN,
        )
        val second = pair(2)
        assertIs<DirectNativeResultV4.Success<Unit>>(
            handle.appendCheckPairs(append(batch = 2, pair = second)),
        )
        assertEquals(1, methods.appendCalls)

        val commit = commit(first)
        assertFailure(
            handle.commitNomination(commit),
            DirectNativeSyntheticReasonV4.NOMINATION_NOT_VALIDATED,
        )
        methods.events += pairValidatedEvent(first)
        assertEquals(
            DirectNativeEventKindV4.PAIR_VALIDATED,
            assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll()).value?.kind,
        )
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.commitNomination(commit))
        assertEquals(1, methods.commitCalls)
        assertContentEquals(first.encode(), methods.lastCommitBlobs!![3])
        assertFailure(
            handle.commitNomination(commit),
            DirectNativeSyntheticReasonV4.NOMINATION_NOT_VALIDATED,
        )
        handle.close()
    }

    @Test
    fun successfulPromotionUsesOnlyOpaqueTokenAndDisablesOrdinaryV4Calls() {
        val methods = FakeV4Methods()
        val handle = createdHandle(methods)
        val winner = pair(1)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.gather(gather()))
        methods.events += candidateBatchEvent(batch = 1, count = 1, total = 1, complete = true)
        assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll())
        assertIs<DirectNativeResultV4.Success<Unit>>(
            handle.beginChecks(begin(pairs = listOf(winner), total = 1, complete = true)),
        )
        methods.events += pairValidatedEvent(winner)
        assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll())

        var receivedOpaqueToken = 0L
        assertIs<DirectNativeResultV4.Success<Unit>>(
            handle.promoteNomination(commit(winner)) { token ->
                receivedOpaqueToken = token
                0
            },
        )
        assertEquals(41L, receivedOpaqueToken)
        assertEquals(0, methods.commitCalls)
        assertFailure(handle.poll(), DirectNativeSyntheticReasonV4.HANDLE_CLOSED)
        assertFailure(
            handle.cancel(7, ByteArray(16) { 7 }),
            DirectNativeSyntheticReasonV4.HANDLE_CLOSED,
        )
        assertFailure(
            handle.promoteNomination(commit(winner)) { error("must not run twice") },
            DirectNativeSyntheticReasonV4.HANDLE_CLOSED,
        )
        assertEquals(0, methods.cancelCalls)
        handle.close()
        handle.close()
        assertEquals(1, methods.destroyCalls)
    }

    @Test
    fun failedPromotionConsumesAttemptAndDisablesEveryOrdinaryV4Call() {
        val methods = FakeV4Methods()
        val handle = createdHandle(methods)
        val winner = pair(1)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.gather(gather()))
        methods.events += candidateBatchEvent(batch = 1, count = 1, total = 1, complete = true)
        assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll())
        assertIs<DirectNativeResultV4.Success<Unit>>(
            handle.beginChecks(begin(pairs = listOf(winner), total = 1, complete = true)),
        )
        methods.events += pairValidatedEvent(winner)
        assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll())

        var promotionCalls = 0
        val failure = assertIs<DirectNativeResultV4.Failure>(
            handle.promoteNomination(commit(winner)) {
                promotionCalls++
                DirectNativeStatusV5.PROTOCOL
            },
        )
        assertEquals(DirectNativeSyntheticReasonV4.FFI_CALL_FAILED, failure.relayOnly.reason)
        assertEquals(DirectNativeStatusV5.PROTOCOL, failure.nativeStatus)
        assertEquals(1, promotionCalls)

        assertFailure(handle.poll(), DirectNativeSyntheticReasonV4.HANDLE_CLOSED)
        assertFailure(
            handle.cancel(7, ByteArray(16) { 7 }),
            DirectNativeSyntheticReasonV4.HANDLE_CLOSED,
        )
        assertFailure(
            handle.networkChanged(7, ByteArray(16) { 7 }),
            DirectNativeSyntheticReasonV4.HANDLE_CLOSED,
        )
        assertFailure(
            handle.promoteNomination(commit(winner)) {
                promotionCalls++
                DirectNativeStatusV5.OK
            },
            DirectNativeSyntheticReasonV4.HANDLE_CLOSED,
        )
        assertEquals(1, promotionCalls)
        assertEquals(0, methods.cancelCalls)
        handle.close()
        handle.close()
        assertEquals(1, methods.destroyCalls)
    }

    @Test
    fun staleAndMalformedEventsNeverBecomeCurrentAndNonContiguousGenerationRetiresHandle() {
        val methods = FakeV4Methods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.gather(gather()))
        methods.events += stateEvent(epoch = 6, attempt = ByteArray(16) { 6 }, generation = 1)
        val stale = assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll()).value!!
        assertEquals(DirectNativeEventKindV4.IGNORED_STALE, stale.kind)
        assertEquals(7, stale.epoch)
        methods.events += stateEvent(generation = 1)
        assertFailure(handle.poll(), DirectNativeSyntheticReasonV4.ABI_MISMATCH)
        assertEquals(1, methods.destroyCalls)

        val gapMethods = FakeV4Methods()
        val gapHandle = createdHandle(gapMethods)
        assertIs<DirectNativeResultV4.Success<Unit>>(gapHandle.gather(gather()))
        gapMethods.events += stateEvent(generation = 2)
        assertFailure(gapHandle.poll(), DirectNativeSyntheticReasonV4.ABI_MISMATCH)
        assertEquals(1, gapMethods.destroyCalls)
    }

    @Test
    fun onlyCurrentPolledSignRequestCanReceiveOwnedSignature() {
        val methods = FakeV4Methods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResultV4.Success<Unit>>(handle.gather(gather()))
        assertFailure(
            handle.submitSignature(7, ByteArray(16) { 7 }, 41, ByteArray(64) { 2 }),
            DirectNativeSyntheticReasonV4.SIGNATURE_NOT_REQUESTED,
        )
        methods.events += signEvent(41)
        assertEquals(
            DirectNativeEventKindV4.SIGN_REQUEST,
            assertIs<DirectNativeResultV4.Success<DirectNativeEventV4?>>(handle.poll()).value?.kind,
        )
        val signature = ByteArray(64) { 2 }
        assertIs<DirectNativeResultV4.Success<Unit>>(
            handle.submitSignature(7, ByteArray(16) { 7 }, 41, signature),
        )
        signature.fill(99)
        assertContentEquals(ByteArray(64) { 2 }, methods.lastSignature)
        assertEquals(1, methods.signatureCalls)
        handle.close()
    }

    private fun createdHandle(methods: FakeV4Methods): DirectNativeHandleV4 =
        assertIs<DirectNativeResultV4.Success<DirectNativeHandleV4>>(
            JniDirectNetworkCoreV4 { methods }.create(DirectNativeCreateConfigV4(enabled = true)),
        ).value

    private fun gather(
        attemptId: ByteArray = ByteArray(16) { 7 },
        bindIp: ByteArray = byteArrayOf(10, 0, 0, 2),
    ) = DirectNativeGatherRequestV4(
        epoch = 7,
        attemptId = attemptId,
        networkHandle = 91,
        localBind = DirectNativeSocketAddressV4(bindIp, 0),
        advertisedLocalIp = bindIp,
        networkCost = 10,
        candidateExpiresAtMillis = 9_000_000,
        stunServers = emptyList(),
        gatherTimeoutMillis = 500,
    )

    private fun candidate(id: Int, octet: Int) = DirectNativeCandidateV4(
        candidateId = ByteArray(16) { id.toByte() },
        relatedCandidateId = ByteArray(16),
        endpoint = DirectNativeSocketAddressV4(byteArrayOf(10, 0, 0, octet.toByte()), 50_000 + id),
        kind = DirectNativeCandidateKindV4.HOST,
        priority = 2_130_706_431,
        networkCost = 10,
        expiresAtMillis = 9_000_000,
    )

    private fun pair(rank: Int) = DirectNativeCandidatePairV4(
        rank,
        candidate(rank * 2 + 1, rank * 2 + 1),
        candidate(rank * 2 + 2, rank * 2 + 2),
    )

    private fun begin(
        pairs: List<DirectNativeCandidatePairV4>,
        total: Int,
        complete: Boolean,
    ) = DirectNativeBeginChecksRequestV4(
        epoch = 7,
        attemptId = ByteArray(16) { 7 },
        connectionId = ByteArray(16) { 8 },
        localDeviceId = ByteArray(16) { 9 },
        peerDeviceId = ByteArray(16) { 10 },
        networkHandle = 91,
        nowMillis = 8_000_000,
        timeoutMillis = 1_500,
        role = DirectNativeRoleV4.INITIATOR,
        ticket = ByteArray(64) { 11 },
        pairBatchSequence = 1,
        totalPairCount = total,
        planComplete = complete,
        serverSigningPublicKey = ByteArray(32) { 12 },
        ticketId = ByteArray(16) { 13 },
        checkRoundId = ByteArray(16) { 14 },
        connectivityNonce = ByteArray(16) { 15 },
        localSendProbeHmacKey = ByteArray(32) { 16 },
        peerReceiveProbeHmacKey = ByteArray(32) { 17 },
        pairs = pairs,
    )

    private fun append(batch: Int, pair: DirectNativeCandidatePairV4) =
        DirectNativeAppendCheckPairsRequestV4(
            epoch = 7,
            attemptId = ByteArray(16) { 7 },
            checkRoundId = ByteArray(16) { 14 },
            pairBatchSequence = batch,
            totalPairCount = 2,
            planComplete = true,
            pairs = listOf(pair),
        )

    private fun commit(pair: DirectNativeCandidatePairV4) =
        DirectNativeCommitNominationRequestV4(
            epoch = 7,
            attemptId = ByteArray(16) { 7 },
            checkRoundId = ByteArray(16) { 14 },
            nominationId = ByteArray(16) { 18 },
            winningPair = pair,
            role = DirectNativeRoleV4.INITIATOR,
            timeoutMillis = 2_000,
            selectionConfirmed = true,
            peerCertificateSha256 = ByteArray(32) { 19 },
            peerDeviceSigningPublicKey = ByteArray(32) { 20 },
            localProofNonce = ByteArray(16) { 21 },
            nowMillis = 8_000_100,
        )

    private fun stateEvent(
        epoch: Long = 7,
        attempt: ByteArray = ByteArray(16) { 7 },
        generation: Long,
    ) = FakeV4Event(
        longArrayOf(4, 2_224, 2, 2, 0, 0, epoch, generation, 0, 0, 0),
        attempt,
        ByteArray(16),
        ByteArray(2_080),
    )

    private fun candidateBatchEvent(batch: Int, count: Int, total: Int, complete: Boolean): FakeV4Event {
        val payload = ByteBuffer.allocate(2_080).order(ByteOrder.LITTLE_ENDIAN)
        payload.putInt(0, 4)
        payload.putInt(4, 1_088)
        payload.position(8)
        payload.putLong(7)
        payload.put(ByteArray(16) { 7 })
        payload.putInt(32, batch)
        payload.putInt(36, count)
        payload.putInt(40, total)
        payload.putInt(44, if (complete) 1 else 0)
        if (count == 1) putCandidateAbi(payload, 48, candidate(1, 1))
        return FakeV4Event(
            longArrayOf(4, 2_224, 5, 2, 0, 1_088, 7, 1, 0, 0, 0),
            ByteArray(16) { 7 }, ByteArray(16), payload.array(),
        )
    }

    private fun pairValidatedEvent(pair: DirectNativeCandidatePairV4): FakeV4Event {
        val payload = ByteBuffer.allocate(2_080).order(ByteOrder.LITTLE_ENDIAN)
        payload.putInt(0, 4)
        payload.putInt(4, 360)
        payload.position(8)
        payload.putLong(7)
        payload.put(ByteArray(16) { 7 })
        payload.put(ByteArray(16) { 14 })
        putPairAbi(payload, 48, pair)
        return FakeV4Event(
            longArrayOf(4, 2_224, 6, 3, 0, 360, 7, 2, 0, 0, 0),
            ByteArray(16) { 7 }, ByteArray(16), payload.array(),
        )
    }

    private fun signEvent(request: Long): FakeV4Event {
        val payload = ByteBuffer.allocate(2_080).order(ByteOrder.LITTLE_ENDIAN)
        payload.putInt(0, 4)
        payload.putInt(4, 2_080)
        payload.putInt(8, 32)
        return FakeV4Event(
            longArrayOf(4, 2_224, 4, 5, 0, 2_080, 7, 1, request, 0, 0),
            ByteArray(16) { 7 }, ByteArray(16), payload.array(),
        )
    }

    private fun putPairAbi(buffer: ByteBuffer, offset: Int, pair: DirectNativeCandidatePairV4) {
        buffer.putInt(offset, 4)
        buffer.putInt(offset + 4, 288)
        buffer.putInt(offset + 8, pair.rank)
        putCandidateAbi(buffer, offset + 16, pair.initiator)
        putCandidateAbi(buffer, offset + 144, pair.responder)
    }

    private fun putCandidateAbi(buffer: ByteBuffer, offset: Int, candidate: DirectNativeCandidateV4) {
        buffer.putInt(offset, 4)
        buffer.putInt(offset + 4, 128)
        buffer.position(offset + 8)
        buffer.put(candidate.candidateId)
        buffer.put(candidate.relatedCandidateId)
        buffer.putInt(offset + 40, 4)
        buffer.putInt(offset + 44, 48)
        buffer.position(offset + 48)
        buffer.put(candidate.endpoint.ipAddress.copyOf(16))
        buffer.putInt(offset + 64, candidate.endpoint.ipAddress.size)
        buffer.putInt(offset + 68, candidate.endpoint.port)
        buffer.putInt(offset + 88, candidate.kind.code)
        buffer.putInt(offset + 92, 1)
        buffer.putInt(offset + 96, candidate.priority)
        buffer.putInt(offset + 100, candidate.networkCost)
        buffer.putLong(offset + 104, candidate.expiresAtMillis)
    }

    private fun assertFailure(result: DirectNativeResultV4<*>, reason: Int) {
        assertEquals(reason, assertIs<DirectNativeResultV4.Failure>(result).relayOnly.reason)
    }

    private fun compatibleAbi() = longArrayOf(
        4, 32, 48, 256, 128, 1_088, 288, 4_656, 2_400, 504,
        48, 192, 2_080, 360, 80, 376, 400, 2_080, 2_224,
        40, 128, 40, 48, 16, 144, 128, 2_256, 2_320, 64, 64,
        432, 32, 64, 112, 2_192, 4_624,
    )

    private data class FakeV4Event(
        val metadata: LongArray,
        val attempt: ByteArray,
        val endpoint: ByteArray,
        val payload: ByteArray,
    )

    private class FakeV4Methods(
        private val abi: LongArray = longArrayOf(
            4, 32, 48, 256, 128, 1_088, 288, 4_656, 2_400, 504,
            48, 192, 2_080, 360, 80, 376, 400, 2_080, 2_224,
            40, 128, 40, 48, 16, 144, 128, 2_256, 2_320, 64, 64,
            432, 32, 64, 112, 2_192, 4_624,
        ),
    ) : DirectNativeMethodsV4 {
        var createCalls = 0
        var gatherCalls = 0
        var beginCalls = 0
        var appendCalls = 0
        var commitCalls = 0
        var cancelCalls = 0
        var signatureCalls = 0
        var destroyCalls = 0
        var lastGatherBlobs: Array<ByteArray>? = null
        var lastBeginBlobs: Array<ByteArray>? = null
        var lastCommitBlobs: Array<ByteArray>? = null
        var lastSignature: ByteArray? = null
        val events = ArrayDeque<FakeV4Event>()

        override fun abiContractV4() = abi.copyOf()
        override fun createV4(enabled: Boolean, queueCapacity: Int, outHandle: LongArray): Int {
            createCalls++
            outHandle[0] = 41
            return 0
        }
        override fun gatherV4(
            handle: Long, scalars: LongArray, blobs: Array<ByteArray>, stunPorts: IntArray,
        ): Int {
            gatherCalls++
            lastGatherBlobs = blobs.map(ByteArray::copyOf).toTypedArray()
            return 0
        }
        override fun beginChecksV4(
            handle: Long, scalars: LongArray, blobs: Array<ByteArray>,
            encodedPairs: Array<ByteArray>,
        ): Int {
            beginCalls++
            lastBeginBlobs = blobs.map(ByteArray::copyOf).toTypedArray()
            return 0
        }
        override fun appendCheckPairsV4(
            handle: Long, scalars: LongArray, blobs: Array<ByteArray>,
            encodedPairs: Array<ByteArray>,
        ): Int {
            appendCalls++
            return 0
        }
        override fun commitNominationV4(
            handle: Long, scalars: LongArray, blobs: Array<ByteArray>,
        ): Int {
            commitCalls++
            lastCommitBlobs = blobs.map(ByteArray::copyOf).toTypedArray()
            return 0
        }
        override fun cancelV4(handle: Long, epoch: Long, attemptId: ByteArray): Int {
            cancelCalls++
            return 0
        }
        override fun networkChangedV4(handle: Long, epoch: Long, attemptId: ByteArray) = 0
        override fun submitSignatureV4(
            handle: Long, epoch: Long, attemptId: ByteArray, requestId: Long, signature: ByteArray,
        ): Int {
            signatureCalls++
            lastSignature = signature.copyOf()
            return 0
        }
        override fun pollV4(
            handle: Long, metadata: LongArray, attemptId: ByteArray, endpointIp: ByteArray,
            payload: ByteArray,
        ): Int {
            val event = events.pollFirst() ?: return 0
            event.metadata.copyInto(metadata)
            event.attempt.copyInto(attemptId)
            event.endpoint.copyInto(endpointIp)
            event.payload.copyInto(payload)
            return 1
        }
        override fun destroyV4(handle: Long): Int {
            destroyCalls++
            return 0
        }
    }
}

package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.ArrayDeque
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class JniDirectNetworkCoreTest {
    @Test
    fun loadAbiAndFfiFailuresSynchronouslyRemainRelayOnly() {
        assertRelayFailure(
            JniDirectNetworkCore { error("missing native library") }.create(),
            DirectNativeSyntheticReason.NATIVE_UNAVAILABLE,
        )
        val incompatible = FakeMethods(abi = compatibleAbi().copy(gatherSize = 1))
        assertRelayFailure(
            JniDirectNetworkCore { incompatible }.create(),
            DirectNativeSyntheticReason.ABI_MISMATCH,
        )
        assertEquals(0, incompatible.createCalls)

        val methods = FakeMethods(gatherStatus = -4)
        val handle = createdHandle(methods)
        val failed = handle.gather(gather())
        assertRelayFailure(failed, DirectNativeSyntheticReason.FFI_CALL_FAILED)
        assertEquals(-4, assertIs<DirectNativeResult.Failure>(failed).nativeStatus)
        assertEquals(1, methods.destroyCalls)
        assertFalse(handle.directRoutingPermitted)
    }

    @Test
    fun gatherCandidatesMustBeExactlyMarkedBeforeNominateAndUsesOwnedCopies() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        val attemptSource = ByteArray(16) { 7 }
        val bindIpSource = byteArrayOf(10, 0, 0, 2)
        val gather = gather(attemptId = attemptSource, bindIp = bindIpSource)
        attemptSource[0] = 99
        bindIpSource[0] = 99

        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather))
        assertEquals(1, methods.gatherCalls)
        assertEquals(gather.networkHandle, methods.lastGatherScalars!![1])
        assertContentEquals(ByteArray(16) { 7 }, methods.lastGatherBlobs!![0])
        assertContentEquals(byteArrayOf(10, 0, 0, 2), methods.lastGatherBlobs!![1])

        val nomination = nomination()
        assertRelayFailure(
            handle.nominate(nomination),
            DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
        )
        assertEquals(0, methods.nominateCalls)

        methods.enqueue(candidatesEvent(epoch = 7, attempt = ByteArray(16) { 7 }))
        val ready = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertEquals(DirectNativeEventKind.CANDIDATES_READY, ready.kind)
        assertEquals(listOf(boundEndpoint()), ready.candidates)
        assertTrue(ready.candidates[0].ipAddress !== ready.endpoint!!.ipAddress)

        assertRelayFailure(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation + 1,
                ready.candidates,
            ),
            DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
        )
        assertRelayFailure(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation,
                listOf(DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 99), 50_000)),
            ),
            DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
        )
        assertIs<DirectNativeResult.Success<Unit>>(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation,
                ready.candidates,
            ),
        )
        assertIs<DirectNativeResult.Success<Unit>>(handle.nominate(nomination))
        assertEquals(1, methods.nominateCalls)
        assertEquals(7, methods.lastNominateScalars!![0])
        assertContentEquals(nomination.ticket, methods.lastNominateBlobs!![6])
        assertTrue(methods.lastNominateBlobs!![6] !== nomination.ticket)
        assertRelayFailure(
            handle.nominate(nomination),
            DirectNativeSyntheticReason.NOMINATION_ALREADY_SUBMITTED,
        )
        assertEquals(1, methods.nominateCalls)
        assertFalse(handle.directRoutingPermitted)
        handle.close()
    }

    @Test
    fun staleEpochAttemptCancelAndNetworkChangeAreExactlyFenced() {
        val cancelMethods = FakeMethods()
        val cancelHandle = createdHandle(cancelMethods)
        assertIs<DirectNativeResult.Success<Unit>>(cancelHandle.gather(gather()))
        assertRelayFailure(
            cancelHandle.cancel(6, ByteArray(16) { 6 }),
            DirectNativeSyntheticReason.STALE_OWNER,
        )
        assertRelayFailure(
            cancelHandle.cancel(7, ByteArray(16) { 6 }),
            DirectNativeSyntheticReason.STALE_OWNER,
        )
        assertEquals(0, cancelMethods.cancelCalls)
        assertIs<DirectNativeResult.Success<Unit>>(
            cancelHandle.cancel(7, ByteArray(16) { 7 }),
        )
        assertEquals(1, cancelMethods.cancelCalls)
        assertEquals(1, cancelMethods.destroyCalls)

        val networkMethods = FakeMethods()
        val networkHandle = createdHandle(networkMethods)
        assertIs<DirectNativeResult.Success<Unit>>(networkHandle.gather(gather(epoch = 8)))
        assertRelayFailure(
            networkHandle.networkChanged(7, ByteArray(16) { 7 }),
            DirectNativeSyntheticReason.STALE_OWNER,
        )
        assertEquals(0, networkMethods.networkChangedCalls)
        assertIs<DirectNativeResult.Success<Unit>>(
            networkHandle.networkChanged(8, ByteArray(16) { 7 }),
        )
        assertEquals(1, networkMethods.networkChangedCalls)
        assertEquals(1, networkMethods.destroyCalls)
    }

    @Test
    fun signatureInboxAcceptsOnlyCurrentPolledRequestAndOwnsSignature() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        prepareNomination(handle, methods)
        val attempt = ByteArray(16) { 7 }
        assertRelayFailure(
            handle.submitSignature(6, attempt, 41, ByteArray(64) { 3 }),
            DirectNativeSyntheticReason.STALE_OWNER,
        )
        assertRelayFailure(
            handle.submitSignature(7, ByteArray(16) { 6 }, 41, ByteArray(64) { 3 }),
            DirectNativeSyntheticReason.STALE_OWNER,
        )
        assertRelayFailure(
            handle.submitSignature(7, attempt, 41, ByteArray(64) { 3 }),
            DirectNativeSyntheticReason.SIGNATURE_NOT_REQUESTED,
        )
        methods.enqueue(signRequestEvent(7, attempt, 41))
        assertEquals(
            DirectNativeEventKind.SIGN_REQUEST,
            assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value?.kind,
        )
        val signature = ByteArray(64) { 3 }
        assertIs<DirectNativeResult.Success<Unit>>(handle.submitSignature(7, attempt, 41, signature))
        signature.fill(99)
        assertContentEquals(ByteArray(64) { 3 }, methods.lastSignature)
        assertRelayFailure(
            handle.submitSignature(7, attempt, 41, ByteArray(64) { 4 }),
            DirectNativeSyntheticReason.SIGNATURE_NOT_REQUESTED,
        )
        assertEquals(1, methods.signatureCalls)
        handle.close()
    }

    @Test
    fun androidNetworkHandleIsMandatoryAndNominationMustReuseGatherOwner() {
        assertFailsWith<IllegalArgumentException> { gather(networkHandle = 0) }
        assertFailsWith<IllegalArgumentException> { nomination(networkHandle = 0) }

        val methods = FakeMethods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather()))
        methods.enqueue(candidatesEvent(7, ByteArray(16) { 7 }))
        val ready = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertIs<DirectNativeResult.Success<Unit>>(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation,
                ready.candidates,
            ),
        )
        assertRelayFailure(
            handle.nominate(nomination(networkHandle = 4_321)),
            DirectNativeSyntheticReason.ABI_MISMATCH,
        )
        assertEquals(0, methods.nominateCalls)
        assertEquals(1, methods.destroyCalls)
        assertFalse(handle.directRoutingPermitted)
    }

    @Test
    fun staleNativeEventCannotChangeCurrentCandidatesOwner() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather(epoch = 7)))
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather(epoch = 8)))
        methods.enqueue(candidatesEvent(7, ByteArray(16) { 7 }))
        val stale = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertEquals(DirectNativeEventKind.IGNORED_STALE, stale.kind)
        assertEquals(8, stale.epoch)
        assertEquals(7, stale.requestId)
        assertRelayFailure(
            handle.markCandidatesExchanged(
                8,
                ByteArray(16) { 7 },
                stale.generation,
                stale.candidates,
            ),
            DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
        )
        assertEquals(0, methods.nominateCalls)
        handle.close()
    }

    @Test
    fun mismatchedCandidateSnapshotCannotAuthorizeNominationOrTouchOtherTransports() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather()))
        methods.enqueue(candidatesEvent(7, ByteArray(16) { 7 }))
        val ready = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertRelayFailure(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation,
                ready.candidates + DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 4), 50_002),
            ),
            DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
        )
        assertRelayFailure(
            handle.nominate(nomination()),
            DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
        )
        assertEquals(0, methods.cancelCalls)
        assertEquals(0, methods.destroyCalls)
        assertEquals(0, methods.nominateCalls)
        assertFalse(handle.directRoutingPermitted)
        handle.close()
        assertEquals(1, methods.destroyCalls)
    }

    @Test
    fun advertisedHostMayDifferFromRetainedBindAddressButMustShareSocketPortAndFamily() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather()))
        val advertisedHost = DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 99), 50_000)
        methods.enqueue(candidatesEvent(
            epoch = 7,
            attempt = ByteArray(16) { 7 },
            hostCandidate = advertisedHost,
        ))
        val ready = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertEquals(boundEndpoint(), ready.endpoint)
        assertEquals(listOf(advertisedHost), ready.candidates)
        assertIs<DirectNativeResult.Success<Unit>>(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation,
                ready.candidates,
            ),
        )
        assertIs<DirectNativeResult.Success<Unit>>(handle.nominate(nomination()))
        handle.close()
    }

    @Test
    fun replayedEventGenerationCannotReopenConsumedSignatureRequest() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        prepareNomination(handle, methods)
        val attempt = ByteArray(16) { 7 }
        methods.enqueue(signRequestEvent(7, attempt, 41).copy(generation = 2))
        assertEquals(
            DirectNativeEventKind.SIGN_REQUEST,
            assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value?.kind,
        )
        assertIs<DirectNativeResult.Success<Unit>>(
            handle.submitSignature(7, attempt, 41, ByteArray(64) { 3 }),
        )
        methods.enqueue(signRequestEvent(7, attempt, 41).copy(generation = 2))
        assertRelayFailure(handle.poll(), DirectNativeSyntheticReason.ABI_MISMATCH)
        assertEquals(1, methods.signatureCalls)
        assertEquals(1, methods.destroyCalls)
        assertFalse(handle.directRoutingPermitted)
    }

    @Test
    fun nativeAbiSurfaceHasNoRelaySessionRendererOrAckOperations() {
        assertEquals(
            setOf(
                "abiContract", "create", "gather", "nominate", "cancel", "networkChanged",
                "submitSignature", "poll", "destroy",
            ),
            DirectNativeMethods::class.java.declaredMethods.map { it.name }.toSet(),
        )
    }

    @Test
    fun nativeDirectFailureEventRetiresOnlyShadowHandleToRelayOnly() {
        val methods = FakeMethods()
        val handle = createdHandle(methods)
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather()))
        methods.enqueue(FakeEvent(
            kind = DirectNativeEventKind.DIRECT_FAILED,
            state = DirectNativePathState.RELAY_ONLY,
            epoch = 7,
            attempt = ByteArray(16) { 7 },
            reason = 7,
        ))
        val failure = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertEquals(DirectNativeEventKind.DIRECT_FAILED, failure.kind)
        assertEquals(DirectNativePathState.RELAY_ONLY, failure.state)
        assertEquals(1, methods.destroyCalls)
        assertFalse(handle.directRoutingPermitted)
    }

    @Test
    fun closeWaitsForSerializedNativeCallerAndDestroysExactlyOnce() {
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val methods = FakeMethods(gatherBlock = {
            entered.countDown()
            assertTrue(release.await(2, TimeUnit.SECONDS))
        })
        val handle = createdHandle(methods)
        val call = thread { handle.gather(gather()) }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val close = thread { handle.close() }
        Thread.sleep(30)
        assertEquals(0, methods.destroyCalls)
        release.countDown()
        call.join(2_000)
        close.join(2_000)
        handle.close()
        assertEquals(1, methods.destroyCalls)
    }

    @Test
    fun defaultCreateIsShadowDisabledAndPerformsNoNetworkCommand() {
        val methods = FakeMethods()
        val core = JniDirectNetworkCore { methods }
        assertFalse(core.directRoutingPermitted)
        val handle = assertIs<DirectNativeResult.Success<DirectNativeHandle>>(core.create()).value
        handle.use {
            assertEquals(false, methods.lastEnabled)
            assertEquals(0, methods.gatherCalls)
            assertEquals(0, methods.nominateCalls)
            assertNull(assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(it.poll()).value)
        }
    }

    private fun createdHandle(methods: FakeMethods) =
        assertIs<DirectNativeResult.Success<DirectNativeHandle>>(
            JniDirectNetworkCore { methods }.create(DirectNativeCreateConfig(enabled = true)),
        ).value

    private fun prepareNomination(handle: DirectNativeHandle, methods: FakeMethods) {
        assertIs<DirectNativeResult.Success<Unit>>(handle.gather(gather()))
        methods.enqueue(candidatesEvent(7, ByteArray(16) { 7 }))
        val ready = assertIs<DirectNativeResult.Success<DirectNativeEvent?>>(handle.poll()).value!!
        assertIs<DirectNativeResult.Success<Unit>>(
            handle.markCandidatesExchanged(
                ready.epoch,
                ready.attemptId,
                ready.generation,
                ready.candidates,
            ),
        )
        assertIs<DirectNativeResult.Success<Unit>>(handle.nominate(nomination()))
    }

    private fun assertRelayFailure(result: DirectNativeResult<*>, reason: Int) {
        val failure = assertIs<DirectNativeResult.Failure>(result)
        assertEquals(DirectNativePathState.RELAY_ONLY, failure.relayOnly.state)
        assertEquals(reason, failure.relayOnly.reason)
    }

    private fun gather(
        epoch: Long = 7,
        attemptId: ByteArray = ByteArray(16) { 7 },
        bindIp: ByteArray = byteArrayOf(10, 0, 0, 2),
        networkHandle: Long = 1_234,
    ) = DirectNativeGatherRequest(
        epoch = epoch,
        attemptId = attemptId,
        networkHandle = networkHandle,
        localBind = DirectNativeSocketAddress(bindIp, 0),
        advertisedLocalIp = byteArrayOf(10, 0, 0, 2),
        stunServers = listOf(DirectNativeSocketAddress(byteArrayOf(1, 1, 1, 1), 3_478)),
        gatherTimeoutMillis = 1_000,
    )

    private fun nomination(networkHandle: Long = 1_234) = DirectNativeNominateRequest(
        epoch = 7,
        attemptId = ByteArray(16) { 7 },
        connectionId = ByteArray(16) { 2 },
        localDeviceId = ByteArray(16) { 3 },
        peerDeviceId = ByteArray(16) { 4 },
        role = DirectNativeRole.INITIATOR,
        timeoutMillis = 1_000,
        networkHandle = networkHandle,
        localBind = boundEndpoint(),
        remoteCandidate = DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 3), 50_001),
        ticket = ByteArray(80) { 5 },
        serverSigningPublicKey = ByteArray(32) { 6 },
        peerCertificateSha256 = ByteArray(32) { 7 },
        peerDeviceSigningPublicKey = ByteArray(32) { 8 },
        localSendProbeHmacKey = ByteArray(32) { 9 },
        peerReceiveProbeHmacKey = ByteArray(32) { 19 },
        ticketId = ByteArray(16) { 10 },
        nominationId = ByteArray(16) { 11 },
        initiatorCandidateId = ByteArray(16) { 12 },
        responderCandidateId = ByteArray(16) { 13 },
        localProbeNonce = ByteArray(16) { 14 },
        peerProbeNonce = ByteArray(16) { 15 },
        localProofNonce = ByteArray(16) { 16 },
        nowMillis = 1_900_000_000_000,
    )

    private fun boundEndpoint() = DirectNativeSocketAddress(byteArrayOf(10, 0, 0, 2), 50_000)

    private fun candidatesEvent(
        epoch: Long,
        attempt: ByteArray,
        hostCandidate: DirectNativeSocketAddress = boundEndpoint(),
    ): FakeEvent {
        val payload = ByteBuffer.allocate(DIRECT_NATIVE_CANDIDATES_BYTES)
            .order(ByteOrder.LITTLE_ENDIAN)
            .putInt(1)
            .putInt(0)
            .putAddress(hostCandidate)
            .put(ByteArray(48))
            .array()
        return FakeEvent(
            kind = DirectNativeEventKind.CANDIDATES_READY,
            state = DirectNativePathState.GATHERING,
            epoch = epoch,
            attempt = attempt,
            endpoint = boundEndpoint(),
            payload = payload,
        )
    }

    private fun signRequestEvent(epoch: Long, attempt: ByteArray, requestId: Long) = FakeEvent(
        kind = DirectNativeEventKind.SIGN_REQUEST,
        state = DirectNativePathState.AUTHENTICATING,
        epoch = epoch,
        attempt = attempt,
        requestId = requestId,
        payload = ByteArray(32) { 8 },
    )

    private fun ByteBuffer.putAddress(value: DirectNativeSocketAddress): ByteBuffer {
        put(value.ipAddress)
        put(ByteArray(16 - value.ipAddress.size))
        putInt(value.ipAddress.size)
        putInt(value.port)
        return this
    }

    private data class FakeEvent(
        val kind: DirectNativeEventKind,
        val state: DirectNativePathState,
        val epoch: Long,
        val attempt: ByteArray,
        val reason: Int = 0,
        val requestId: Long = 0,
        val endpoint: DirectNativeSocketAddress? = null,
        val payload: ByteArray = ByteArray(0),
        val generation: Long? = null,
    )

    private class FakeMethods(
        private val abi: DirectNativeAbiContract = compatibleAbi(),
        private val gatherStatus: Int = 0,
        private val gatherBlock: (() -> Unit)? = null,
    ) : DirectNativeMethods {
        private val events = ArrayDeque<FakeEvent>()
        private var nextGeneration = 1L
        var createCalls = 0
        var gatherCalls = 0
        var nominateCalls = 0
        var cancelCalls = 0
        var networkChangedCalls = 0
        var signatureCalls = 0
        var destroyCalls = 0
        var lastEnabled: Boolean? = null
        var lastGatherScalars: LongArray? = null
        var lastGatherBlobs: Array<ByteArray>? = null
        var lastNominateScalars: LongArray? = null
        var lastNominateBlobs: Array<ByteArray>? = null
        var lastSignature: ByteArray? = null

        override fun abiContract() = longArrayOf(
            abi.abiVersion.toLong(), abi.createSize.toLong(), abi.socketAddressSize.toLong(),
            abi.gatherSize.toLong(), abi.nominateSize.toLong(), abi.candidatesSize.toLong(),
            abi.controlSize.toLong(), abi.signatureSize.toLong(), abi.eventSize.toLong(),
            abi.gatherLocalBindOffset.toLong(), abi.gatherStunServersOffset.toLong(),
            abi.gatherReservedOffset.toLong(), abi.nominateStartV2Offset.toLong(),
            abi.nominateLocalSendProbeKeyOffset.toLong(),
            abi.nominatePeerReceiveProbeKeyOffset.toLong(),
            abi.nominateReservedOffset.toLong(), abi.candidatesArrayOffset.toLong(),
            abi.startTicketOffset.toLong(),
            abi.startPeerCertificateSha256Offset.toLong(), abi.startNowMillisOffset.toLong(),
            abi.eventGenerationOffset.toLong(), abi.eventPayloadOffset.toLong(),
        )

        override fun create(enabled: Boolean, queueCapacity: Int, outHandle: LongArray): Int {
            createCalls++
            lastEnabled = enabled
            outHandle[0] = 9
            return 0
        }

        override fun gather(
            handle: Long,
            scalars: LongArray,
            blobs: Array<ByteArray>,
            stunPorts: IntArray,
        ): Int {
            gatherCalls++
            lastGatherScalars = scalars.copyOf()
            lastGatherBlobs = Array(blobs.size) { blobs[it].copyOf() }
            gatherBlock?.invoke()
            return gatherStatus
        }

        override fun nominate(handle: Long, scalars: LongArray, blobs: Array<ByteArray>): Int {
            nominateCalls++
            lastNominateScalars = scalars.copyOf()
            lastNominateBlobs = Array(blobs.size) { blobs[it].copyOf() }
            return 0
        }

        override fun cancel(handle: Long, epoch: Long, attemptId: ByteArray): Int {
            cancelCalls++
            return 0
        }

        override fun networkChanged(handle: Long, epoch: Long, attemptId: ByteArray): Int {
            networkChangedCalls++
            return 0
        }

        override fun submitSignature(
            handle: Long,
            epoch: Long,
            attemptId: ByteArray,
            requestId: Long,
            signature: ByteArray,
        ): Int {
            signatureCalls++
            lastSignature = signature.copyOf()
            return 0
        }

        override fun poll(
            handle: Long,
            metadata: LongArray,
            attemptId: ByteArray,
            endpointIp: ByteArray,
            payload: ByteArray,
        ): Int {
            val event = events.pollFirst() ?: return 0
            metadata[0] = DIRECT_NATIVE_ABI_VERSION.toLong()
            metadata[1] = 2_136
            metadata[2] = event.kind.code.toLong()
            metadata[3] = event.state.code.toLong()
            metadata[4] = event.reason.toLong()
            metadata[5] = event.payload.size.toLong()
            metadata[6] = event.epoch
            metadata[7] = event.generation ?: nextGeneration++
            metadata[8] = event.requestId
            metadata[9] = (event.endpoint?.ipAddress?.size ?: 0).toLong()
            metadata[10] = (event.endpoint?.port ?: 0).toLong()
            event.attempt.copyInto(attemptId)
            event.endpoint?.ipAddress?.copyInto(endpointIp)
            event.payload.copyInto(payload)
            return 1
        }

        override fun destroy(handle: Long): Int {
            destroyCalls++
            return 0
        }

        fun enqueue(event: FakeEvent) {
            events.addLast(event.copy(attempt = event.attempt.copyOf(), payload = event.payload.copyOf()))
        }
    }

    companion object {
        private fun compatibleAbi() = DirectNativeAbiContract(
            abiVersion = 3,
            createSize = 32,
            socketAddressSize = 24,
            gatherSize = 176,
            nominateSize = 2_696,
            candidatesSize = 80,
            controlSize = 32,
            signatureSize = 176,
            eventSize = 2_136,
            gatherLocalBindOffset = 40,
            gatherStunServersOffset = 88,
            gatherReservedOffset = 144,
            nominateStartV2Offset = 8,
            nominateLocalSendProbeKeyOffset = 2_600,
            nominatePeerReceiveProbeKeyOffset = 2_632,
            nominateReservedOffset = 2_664,
            candidatesArrayOffset = 8,
            startTicketOffset = 208,
            startPeerCertificateSha256Offset = 2_288,
            startNowMillisOffset = 2_552,
            eventGenerationOffset = 32,
            eventPayloadOffset = 88,
        )
    }
}

package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Pull-only ABI v3 JNI boundary. Native workers publish into a bounded queue;
 * this class never receives or owns Relay/WSS, session, renderer, Envelope or
 * durable ACK handles. Shadow success therefore cannot authorize cutover.
 */
internal class JniDirectNetworkCore(
    private val methodsProvider: () -> DirectNativeMethods = ::loadDirectNativeMethods,
) {
    val directRoutingPermitted: Boolean
        get() = false

    fun abiContract(): DirectNativeResult<DirectNativeAbiContract> {
        val methods = runCatching(methodsProvider).getOrElse {
            return nativeFailure(DirectNativeSyntheticReason.NATIVE_UNAVAILABLE)
        }
        val contract = runCatching { DirectNativeAbiContract.from(methods.abiContract()) }.getOrNull()
        return if (contract?.isCompatible == true) {
            DirectNativeResult.Success(contract)
        } else {
            nativeFailure(DirectNativeSyntheticReason.ABI_MISMATCH)
        }
    }

    fun create(config: DirectNativeCreateConfig = DirectNativeCreateConfig()):
        DirectNativeResult<DirectNativeHandle> {
        val methods = runCatching(methodsProvider).getOrElse {
            return nativeFailure(DirectNativeSyntheticReason.NATIVE_UNAVAILABLE)
        }
        val contract = runCatching { DirectNativeAbiContract.from(methods.abiContract()) }.getOrNull()
        if (contract?.isCompatible != true) {
            return nativeFailure(DirectNativeSyntheticReason.ABI_MISMATCH)
        }
        val output = LongArray(1)
        val status = runCatching { methods.create(config.enabled, config.queueCapacity, output) }
            .getOrElse { return nativeFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED) }
        return if (status == 0 && output[0] > 0) {
            DirectNativeResult.Success(DirectNativeHandle(methods, output[0]))
        } else {
            nativeFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED, nativeStatus = status)
        }
    }
}

internal class DirectNativeHandle(
    private val methods: DirectNativeMethods,
    private val token: Long,
) : AutoCloseable {
    private val lock = Any()
    private var closed = false
    private var authoritativeEpoch = 0L
    private var authoritativeAttempt = ByteArray(16)
    private var authoritativeNetworkHandle = 0L
    private var lastEventGeneration = 0L
    private var candidatesReady = false
    private var candidatesExchanged = false
    private var candidatesReadyGeneration = 0L
    private var nominationSubmitted = false
    private var retainedEndpoint: DirectNativeSocketAddress? = null
    private var gatheredCandidates: List<DirectNativeSocketAddress> = emptyList()
    private val pendingSignatures = LinkedHashSet<Long>()

    val directRoutingPermitted: Boolean
        get() = false

    fun gather(request: DirectNativeGatherRequest): DirectNativeResult<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(request.epoch, request.attemptId)
        if (request.epoch <= authoritativeEpoch) {
            return@synchronized staleFailure(request.epoch, request.attemptId)
        }
        authoritativeEpoch = request.epoch
        authoritativeAttempt = request.attemptId.copyOf()
        authoritativeNetworkHandle = request.networkHandle
        candidatesReady = false
        candidatesExchanged = false
        candidatesReadyGeneration = 0
        nominationSubmitted = false
        retainedEndpoint = null
        gatheredCandidates = emptyList()
        pendingSignatures.clear()
        callCurrent(request.epoch, request.attemptId) {
            methods.gather(
                token,
                longArrayOf(
                    request.epoch,
                    request.networkHandle,
                    request.localBind.port.toLong(),
                    request.gatherTimeoutMillis.toLong(),
                ),
                buildList {
                    add(request.attemptId.copyOf())
                    add(request.localBind.ipAddress.copyOf())
                    add(request.advertisedLocalIp.copyOf())
                    request.stunServers.forEach { add(it.ipAddress.copyOf()) }
                }.toTypedArray(),
                request.stunServers.map { it.port }.toIntArray(),
            )
        }
    }

    /**
     * Records that the caller exchanged the exact CandidatesReady snapshot over its own
     * authenticated signalling lane. This boundary never owns WSS or signalling callbacks.
     */
    fun markCandidatesExchanged(
        epoch: Long,
        attemptId: ByteArray,
        generation: Long,
        candidates: List<DirectNativeSocketAddress>,
    ): DirectNativeResult<Unit> {
        val ownedAttempt = attemptId.copyOf()
        val ownedCandidates = candidates.map(DirectNativeSocketAddress::ownedCopy)
        return synchronized(lock) {
            if (closed) return@synchronized closedFailure(epoch, ownedAttempt)
            if (!isCurrent(epoch, ownedAttempt)) return@synchronized staleFailure(epoch, ownedAttempt)
            if (!candidatesReady || nominationSubmitted || generation != candidatesReadyGeneration ||
                ownedCandidates != gatheredCandidates
            ) {
                return@synchronized nativeFailure(
                    DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
                    epoch,
                    ownedAttempt,
                )
            }
            candidatesExchanged = true
            DirectNativeResult.Success(Unit)
        }
    }

    fun nominate(request: DirectNativeNominateRequest): DirectNativeResult<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(request.epoch, request.attemptId)
        if (!isCurrent(request.epoch, request.attemptId)) {
            return@synchronized staleFailure(request.epoch, request.attemptId)
        }
        if (!candidatesReady || !candidatesExchanged) {
            return@synchronized nativeFailure(
                DirectNativeSyntheticReason.CANDIDATES_NOT_EXCHANGED,
                request.epoch,
                request.attemptId,
            )
        }
        if (nominationSubmitted) {
            return@synchronized nativeFailure(
                DirectNativeSyntheticReason.NOMINATION_ALREADY_SUBMITTED,
                request.epoch,
                request.attemptId,
            )
        }
        if (request.localBind != retainedEndpoint ||
            request.networkHandle != authoritativeNetworkHandle
        ) {
            return@synchronized retireFailure(
                DirectNativeSyntheticReason.ABI_MISMATCH,
                request.epoch,
                request.attemptId,
            )
        }
        val scalars = longArrayOf(
            request.epoch,
            request.role.code.toLong(),
            request.timeoutMillis.toLong(),
            request.networkHandle,
            request.localBind.port.toLong(),
            request.remoteCandidate.port.toLong(),
            request.nowMillis,
        )
        val blobs = listOf(
            request.attemptId,
            request.connectionId,
            request.localDeviceId,
            request.peerDeviceId,
            request.localBind.ipAddress,
            request.remoteCandidate.ipAddress,
            request.ticket,
            request.serverSigningPublicKey,
            request.peerCertificateSha256,
            request.peerDeviceSigningPublicKey,
            request.localSendProbeHmacKey,
            request.peerReceiveProbeHmacKey,
            request.ticketId,
            request.nominationId,
            request.initiatorCandidateId,
            request.responderCandidateId,
            request.localProbeNonce,
            request.peerProbeNonce,
            request.localProofNonce,
        ).map(ByteArray::copyOf).toTypedArray()
        val result = callCurrent(request.epoch, request.attemptId) {
            methods.nominate(token, scalars, blobs)
        }
        if (result is DirectNativeResult.Success) nominationSubmitted = true
        result
    }

    fun cancel(epoch: Long, attemptId: ByteArray): DirectNativeResult<Unit> {
        val ownedAttempt = attemptId.copyOf()
        return synchronized(lock) {
            exactControl(epoch, ownedAttempt) { methods.cancel(token, epoch, ownedAttempt.copyOf()) }
        }
    }

    fun networkChanged(epoch: Long, attemptId: ByteArray): DirectNativeResult<Unit> {
        val ownedAttempt = attemptId.copyOf()
        return synchronized(lock) {
            exactControl(epoch, ownedAttempt) {
                methods.networkChanged(token, epoch, ownedAttempt.copyOf())
            }
        }
    }

    fun submitSignature(
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): DirectNativeResult<Unit> {
        val ownedAttempt = attemptId.copyOf()
        val ownedSignature = signature.copyOf()
        return synchronized(lock) {
            if (closed) return@synchronized closedFailure(epoch, ownedAttempt)
            if (!isCurrent(epoch, ownedAttempt)) return@synchronized staleFailure(epoch, ownedAttempt)
            if (requestId !in pendingSignatures) {
                return@synchronized nativeFailure(
                    DirectNativeSyntheticReason.SIGNATURE_NOT_REQUESTED,
                    epoch,
                    ownedAttempt,
                )
            }
            if (ownedSignature.isEmpty() || ownedSignature.size > DIRECT_NATIVE_MAX_SIGNATURE_BYTES) {
                return@synchronized retireFailure(
                    DirectNativeSyntheticReason.FFI_CALL_FAILED,
                    epoch,
                    ownedAttempt,
                )
            }
            val result = callCurrent(epoch, ownedAttempt) {
                methods.submitSignature(
                    token,
                    epoch,
                    ownedAttempt.copyOf(),
                    requestId,
                    ownedSignature.copyOf(),
                )
            }
            if (result is DirectNativeResult.Success) pendingSignatures.remove(requestId)
            result
        }
    }

    fun poll(): DirectNativeResult<DirectNativeEvent?> = synchronized(lock) {
        if (closed) return@synchronized closedFailure()
        val metadata = LongArray(POLL_METADATA_VALUES)
        val attempt = ByteArray(16)
        val endpointIp = ByteArray(16)
        val payload = ByteArray(DIRECT_NATIVE_MAX_EVENT_PAYLOAD_BYTES)
        val status = runCatching { methods.poll(token, metadata, attempt, endpointIp, payload) }
            .getOrElse { return@synchronized retireFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED) }
        when (status) {
            0 -> DirectNativeResult.Success(null)
            1 -> decodeEvent(metadata, attempt, endpointIp, payload)
            else -> retireFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED, nativeStatus = status)
        }
    }

    override fun close() {
        synchronized(lock) { retireLocked() }
    }

    private fun exactControl(
        epoch: Long,
        attemptId: ByteArray,
        operation: () -> Int,
    ): DirectNativeResult<Unit> {
        if (closed) return closedFailure(epoch, attemptId)
        if (!isCurrent(epoch, attemptId)) return staleFailure(epoch, attemptId)
        candidatesReady = false
        candidatesExchanged = false
        candidatesReadyGeneration = 0
        nominationSubmitted = false
        retainedEndpoint = null
        gatheredCandidates = emptyList()
        pendingSignatures.clear()
        val status = runCatching(operation).getOrElse {
            retireLocked()
            return nativeFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED, epoch, attemptId)
        }
        retireLocked()
        return if (status == 0) DirectNativeResult.Success(Unit) else nativeFailure(
            DirectNativeSyntheticReason.FFI_CALL_FAILED,
            epoch,
            attemptId,
            status,
        )
    }

    private fun callCurrent(
        epoch: Long,
        attemptId: ByteArray,
        operation: () -> Int,
    ): DirectNativeResult<Unit> {
        val status = runCatching(operation).getOrElse {
            return retireFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED, epoch, attemptId)
        }
        return if (status == 0) {
            DirectNativeResult.Success(Unit)
        } else {
            retireFailure(DirectNativeSyntheticReason.FFI_CALL_FAILED, epoch, attemptId, status)
        }
    }

    private fun decodeEvent(
        metadata: LongArray,
        attemptId: ByteArray,
        endpointIp: ByteArray,
        payload: ByteArray,
    ): DirectNativeResult<DirectNativeEvent?> {
        val kind = DirectNativeEventKind.from(metadata[2].toInt())
        val state = DirectNativePathState.from(metadata[3].toInt())
        val payloadLength = metadata[5].toInt()
        val generation = metadata[7]
        val ipLength = metadata[9].toInt()
        val endpointPort = metadata[10].toInt()
        if (metadata[0].toInt() != DIRECT_NATIVE_ABI_VERSION || metadata[1] != 2_136L ||
            kind == DirectNativeEventKind.UNKNOWN || state == DirectNativePathState.UNKNOWN ||
            generation <= lastEventGeneration ||
            payloadLength !in 0..payload.size || ipLength !in setOf(0, 4, 16) ||
            endpointPort !in 0..65_535 || (ipLength == 0) != (endpointPort == 0) ||
            (ipLength == 4 && endpointIp.drop(4).any { it != 0.toByte() })
        ) {
            return retireFailure(DirectNativeSyntheticReason.ABI_MISMATCH)
        }
        lastEventGeneration = generation
        val eventEpoch = metadata[6]
        val endpoint = if (ipLength == 0) null else DirectNativeSocketAddress(
            endpointIp.copyOf(ipLength),
            endpointPort,
        )
        val ownedPayload = payload.copyOf(payloadLength)
        if (!validEventShape(
                kind = kind,
                state = state,
                reason = metadata[4].toInt(),
                epoch = eventEpoch,
                generation = generation,
                requestId = metadata[8],
                attemptId = attemptId,
                endpoint = endpoint,
                payloadLength = payloadLength,
            )
        ) {
            return retireFailure(DirectNativeSyntheticReason.ABI_MISMATCH)
        }
        if (kind != DirectNativeEventKind.IGNORED_STALE &&
            !isCurrent(eventEpoch, attemptId)
        ) {
            return DirectNativeResult.Success(
                DirectNativeEvent(
                    kind = DirectNativeEventKind.IGNORED_STALE,
                    state = DirectNativePathState.RELAY_ONLY,
                    reason = 0,
                    epoch = authoritativeEpoch,
                    generation = generation,
                    requestId = eventEpoch,
                    attemptId = attemptId.copyOf(),
                    endpoint = null,
                    payload = ByteArray(0),
                ),
            )
        }
        val candidates = if (kind == DirectNativeEventKind.CANDIDATES_READY) {
            decodeCandidates(ownedPayload, endpoint)
                ?: return retireFailure(DirectNativeSyntheticReason.ABI_MISMATCH)
        } else {
            emptyList()
        }
        if ((kind == DirectNativeEventKind.CANDIDATES_READY && nominationSubmitted) ||
            (kind == DirectNativeEventKind.SIGN_REQUEST && !nominationSubmitted) ||
            (kind == DirectNativeEventKind.SHADOW_READY && !nominationSubmitted)
        ) {
            return retireFailure(DirectNativeSyntheticReason.ABI_MISMATCH)
        }
        if (kind == DirectNativeEventKind.CANDIDATES_READY) {
            candidatesReady = true
            candidatesExchanged = false
            candidatesReadyGeneration = generation
            retainedEndpoint = endpoint?.ownedCopy()
            gatheredCandidates = candidates.map(DirectNativeSocketAddress::ownedCopy)
        }
        if (kind == DirectNativeEventKind.SIGN_REQUEST) pendingSignatures.add(metadata[8])
        if (kind == DirectNativeEventKind.DIRECT_FAILED) {
            retireLocked()
        }
        return DirectNativeResult.Success(
            DirectNativeEvent(
                kind = kind,
                state = state,
                reason = metadata[4].toInt(),
                epoch = eventEpoch,
                generation = generation,
                requestId = metadata[8],
                attemptId = attemptId.copyOf(),
                endpoint = endpoint,
                payload = ownedPayload,
                candidates = candidates,
            ),
        )
    }

    private fun validEventShape(
        kind: DirectNativeEventKind,
        state: DirectNativePathState,
        reason: Int,
        epoch: Long,
        generation: Long,
        requestId: Long,
        attemptId: ByteArray,
        endpoint: DirectNativeSocketAddress?,
        payloadLength: Int,
    ): Boolean {
        if (generation <= 0 || epoch <= 0 || attemptId.size != 16 ||
            attemptId.all { it == 0.toByte() }
        ) return false
        return when (kind) {
            DirectNativeEventKind.IDENTITY_READY ->
                state == DirectNativePathState.GATHERING && reason == 0 && requestId == 0L &&
                    endpoint == null && payloadLength in 1..DIRECT_NATIVE_MAX_EVENT_PAYLOAD_BYTES
            DirectNativeEventKind.STATE_CHANGED ->
                state in setOf(
                    DirectNativePathState.GATHERING,
                    DirectNativePathState.PROBING,
                    DirectNativePathState.AUTHENTICATING,
                ) && reason == 0 && requestId == 0L && endpoint == null && payloadLength == 0
            DirectNativeEventKind.DIRECT_FAILED ->
                state == DirectNativePathState.RELAY_ONLY && reason in 1..15 && requestId == 0L &&
                    endpoint == null && payloadLength == 0
            DirectNativeEventKind.SIGN_REQUEST ->
                state == DirectNativePathState.AUTHENTICATING && reason == 0 && requestId > 0 &&
                    endpoint == null && payloadLength > 0
            DirectNativeEventKind.SHADOW_READY ->
                state == DirectNativePathState.SHADOW_DIRECT && reason == 0 && requestId == 0L &&
                    endpoint != null && payloadLength == 32
            DirectNativeEventKind.IGNORED_STALE ->
                state == DirectNativePathState.RELAY_ONLY && reason == 0 && requestId > 0 &&
                    endpoint == null && payloadLength == 0
            DirectNativeEventKind.CANDIDATES_READY ->
                state == DirectNativePathState.GATHERING && reason == 0 && requestId == 0L &&
                    endpoint != null && payloadLength == DIRECT_NATIVE_CANDIDATES_BYTES
            DirectNativeEventKind.SYNTHETIC_FAILURE,
            DirectNativeEventKind.UNKNOWN,
            -> false
        }
    }

    private fun decodeCandidates(
        payload: ByteArray,
        boundEndpoint: DirectNativeSocketAddress?,
    ): List<DirectNativeSocketAddress>? {
        if (payload.size != DIRECT_NATIVE_CANDIDATES_BYTES || boundEndpoint == null) return null
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val count = buffer.int
        val reserved = buffer.int
        if (count !in 1..DIRECT_NATIVE_MAX_CANDIDATES || reserved != 0) return null
        val result = ArrayList<DirectNativeSocketAddress>(count)
        repeat(DIRECT_NATIVE_MAX_CANDIDATES) { index ->
            val ip = ByteArray(16).also(buffer::get)
            val ipLength = buffer.int
            val port = buffer.int
            if (index >= count) {
                if (ip.any { it != 0.toByte() } || ipLength != 0 || port != 0) return null
            } else {
                if (ipLength !in setOf(4, 16) || port !in 1_024..65_535 ||
                    (ipLength == 4 && ip.drop(4).any { it != 0.toByte() })
                ) return null
                val endpoint = DirectNativeSocketAddress(ip.copyOf(ipLength), port)
                if (endpoint in result) return null
                result.add(endpoint)
            }
        }
        if (result.first().port != boundEndpoint.port ||
            result.first().ipAddress.size != boundEndpoint.ipAddress.size
        ) return null
        return result.map(DirectNativeSocketAddress::ownedCopy)
    }

    private fun isCurrent(epoch: Long, attemptId: ByteArray): Boolean =
        epoch == authoritativeEpoch && attemptId.contentEquals(authoritativeAttempt)

    private fun retireLocked() {
        if (closed) return
        closed = true
        candidatesReady = false
        candidatesExchanged = false
        candidatesReadyGeneration = 0
        nominationSubmitted = false
        retainedEndpoint = null
        gatheredCandidates = emptyList()
        pendingSignatures.clear()
        runCatching { methods.destroy(token) }
    }

    private fun <T> retireFailure(
        reason: Int,
        epoch: Long = authoritativeEpoch,
        attemptId: ByteArray = authoritativeAttempt,
        nativeStatus: Int? = null,
    ): DirectNativeResult<T> {
        retireLocked()
        return nativeFailure(reason, epoch, attemptId, nativeStatus)
    }

    private fun <T> staleFailure(epoch: Long, attemptId: ByteArray): DirectNativeResult<T> =
        nativeFailure(DirectNativeSyntheticReason.STALE_OWNER, epoch, attemptId)

    private fun <T> closedFailure(
        epoch: Long = authoritativeEpoch,
        attemptId: ByteArray = authoritativeAttempt,
    ): DirectNativeResult<T> =
        nativeFailure(DirectNativeSyntheticReason.HANDLE_CLOSED, epoch, attemptId)

    private companion object {
        const val POLL_METADATA_VALUES = 11
    }
}

private val loadedMethods: Result<DirectNativeMethods> by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
    runCatching {
        val methods = JniDirectNativeMethods
        check(DirectNativeAbiContract.from(methods.abiContract())?.isCompatible == true) {
            "TermLink direct JNI ABI mismatch"
        }
        methods
    }
}

private fun loadDirectNativeMethods(): DirectNativeMethods = loadedMethods.getOrThrow()

private fun <T> nativeFailure(
    reason: Int,
    epoch: Long = 0,
    attemptId: ByteArray = ByteArray(16),
    nativeStatus: Int? = null,
): DirectNativeResult<T> = DirectNativeResult.Failure(
    relayOnly = DirectNativeEvent.relayOnly(reason, epoch, attemptId),
    nativeStatus = nativeStatus,
)

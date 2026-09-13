package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Serialized Android owner for the independent Direct data-plane ABI v5.
 *
 * It owns no Relay, session, renderer, durable ACK, Agent, SessionHost, or
 * reconnect policy. Relay/control actions are returned to the existing
 * production owners and every call remains fenced by path + connection +
 * attempt.
 */
internal class JniDirectNetworkCoreV5(
    private val methodsProvider: () -> DirectNativeMethodsV5 = ::loadDirectNativeMethodsV5,
) {
    fun abiContract(): DirectNativeResultV5<DirectNativeAbiContractV5> {
        val methods = runCatching(methodsProvider).getOrElse {
            return nativeFailureV5(DirectNativeSyntheticReasonV5.NATIVE_UNAVAILABLE)
        }
        val contract = runCatching { DirectNativeAbiContractV5.from(methods.abiContractV5()) }
            .getOrNull()
        return if (contract?.isCompatible == true) DirectNativeResultV5.Success(contract)
        else nativeFailureV5(DirectNativeSyntheticReasonV5.ABI_MISMATCH)
    }

    fun create(
        config: DirectNativeCreateConfigV5 = DirectNativeCreateConfigV5(),
    ): DirectNativeResultV5<DirectNativeHandleV5> {
        val methods = runCatching(methodsProvider).getOrElse {
            return nativeFailureV5(DirectNativeSyntheticReasonV5.NATIVE_UNAVAILABLE)
        }
        val contract = runCatching { DirectNativeAbiContractV5.from(methods.abiContractV5()) }
            .getOrNull()
        if (contract?.isCompatible != true) {
            return nativeFailureV5(DirectNativeSyntheticReasonV5.ABI_MISMATCH)
        }
        val output = LongArray(1)
        val status = runCatching {
            methods.createV5(
                config.enabled,
                config.queueCapacityFrames,
                config.queueCapacityBytes,
                config.initialPath?.encode() ?: ByteArray(DirectNativePathV5.PATH_BYTES),
                output,
            )
        }.getOrElse {
            return nativeFailureV5(DirectNativeSyntheticReasonV5.FFI_CALL_FAILED)
        }
        return if (status == DirectNativeStatusV5.OK && output[0] > 0) {
            DirectNativeResultV5.Success(
                DirectNativeHandleV5(methods, output[0], config.initialPath?.ownedCopy()),
            )
        } else {
            nativeFailureV5(
                DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
                nativeStatus = status,
            )
        }
    }
}

internal class DirectNativeHandleV5(
    private val methods: DirectNativeMethodsV5,
    private val token: Long,
    initialPath: DirectNativePathV5?,
) : AutoCloseable {
    private val lock = Any()
    private var closed = false
    private var currentPath = initialPath?.ownedCopy()
    /**
     * Native stages exact Relay recovery under the predecessor fence before it
     * installs a successor. Keep those full fences until their terminal
     * CARRIER action is drained; an epoch alone never authorizes the drain.
     */
    private val drainingPaths = LinkedHashSet<DirectNativePathV5>()
    private val pendingSigningRequests = LinkedHashSet<Long>()

    fun usePath(path: DirectNativePathV5): DirectNativeResultV5<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(path)
        val current = currentPath
        if (current != null && path.epoch <= current.epoch && !current.sameFence(path)) {
            return@synchronized staleFailure(path)
        }
        val status = call { methods.pathUsageV5(token, path.encode()) }
            ?: return@synchronized ffiFailure(path)
        if (status == DirectNativeStatusV5.OK) {
            if (current != null && !current.sameFence(path)) {
                drainingPaths += current.ownedCopy()
            }
            currentPath = path.ownedCopy()
            pendingSigningRequests.clear()
            DirectNativeResultV5.Success(Unit)
        } else {
            statusFailure(path, status)
        }
    }

    fun attach(path: DirectNativePathV5): DirectNativeResultV5<Unit> =
        exactPathOperation(path) { methods.attachV5(token, it) }

    fun promote(
        v4: DirectNativeHandleV4,
        request: DirectNativePromoteRequestV5,
    ): DirectNativeResultV5<Unit> = synchronized(lock) {
        val path = request.path
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        val encoded = request.encode()
        val result = v4.promoteNomination(request.nomination) { v4Token ->
            try {
                methods.promoteV5(token, v4Token, encoded)
            } finally {
                encoded.fill(0)
            }
        }
        when (result) {
            is DirectNativeResultV4.Success -> DirectNativeResultV5.Success(Unit)
            is DirectNativeResultV4.Failure -> nativeFailureV5(
                DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
                path.epoch,
                path.attemptId,
                result.nativeStatus,
            )
        }
    }

    fun receiveSigningPayload(
        path: DirectNativePathV5,
    ): DirectNativeResultV5<DirectNativeSigningPayloadV5?> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        val probe = LongArray(READ_METADATA_VALUES)
        val probeStatus = call {
            methods.receiveSigningPayloadV5(token, path.encode(), probe, ByteArray(0))
        } ?: return@synchronized ffiFailure(path)
        if (probeStatus == DirectNativeStatusV5.NO_DATA) {
            return@synchronized if (probe.all { it == 0L }) DirectNativeResultV5.Success(null)
            else invalidResponse(path)
        }
        // A short-buffer probe reports only required_bytes. The request ID is
        // deliberately left unchanged until the successful dequeuing read.
        if (probeStatus != DirectNativeStatusV5.BUFFER_TOO_SMALL || probe[0] != 0L ||
            probe[1] !in 1..DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5.toLong()
        ) return@synchronized statusOrInvalid(path, probeStatus)
        val output = ByteArray(probe[1].toInt())
        val metadata = LongArray(READ_METADATA_VALUES)
        val status = call {
            methods.receiveSigningPayloadV5(token, path.encode(), metadata, output)
        } ?: return@synchronized ffiFailure(path)
        if (status != DirectNativeStatusV5.OK) {
            output.fill(0)
            return@synchronized statusFailure(path, status)
        }
        if (metadata[0] <= 0L || metadata[1] != probe[1]) {
            output.fill(0)
            return@synchronized invalidResponse(path)
        }
        pendingSigningRequests += metadata[0]
        DirectNativeResultV5.Success(DirectNativeSigningPayloadV5(metadata[0], output))
    }

    fun submitSignature(
        path: DirectNativePathV5,
        requestId: Long,
        signature: ByteArray,
    ): DirectNativeResultV5<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        if (requestId !in pendingSigningRequests ||
            signature.isEmpty() || signature.size > DIRECT_NATIVE_MAX_SIGNATURE_BYTES_V5
        ) return@synchronized nativeFailureV5(
            DirectNativeSyntheticReasonV5.SIGNATURE_NOT_REQUESTED,
            path.epoch,
            path.attemptId,
            DirectNativeStatusV5.INVALID_ARGUMENT,
        )
        val record = ByteBuffer.allocate(SIGNATURE_BYTES).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(0, DIRECT_NATIVE_ABI_VERSION_V5)
            putInt(4, SIGNATURE_BYTES)
            putBytes(8, path.encode())
            putLong(72, requestId)
            putInt(80, signature.size)
            putBytes(88, signature)
        }.array()
        val status = try {
            call { methods.submitSignatureV5(token, record) }
                ?: return@synchronized ffiFailure(path)
        } finally {
            record.fill(0)
        }
        return@synchronized if (status == DirectNativeStatusV5.OK) {
            pendingSigningRequests.remove(requestId)
            DirectNativeResultV5.Success(Unit)
        } else {
            statusFailure(path, status)
        }
    }

    fun pathInfo(path: DirectNativePathV5): DirectNativeResultV5<DirectNativePathInfoV5> =
        synchronized(lock) {
            if (closed) return@synchronized closedFailure(path)
            if (!owns(path)) return@synchronized staleFailure(path)
            val output = ByteArray(PATH_INFO_BYTES)
            val status = call { methods.pathInfoV5(token, path.encode(), output) }
                ?: return@synchronized ffiFailure(path)
            if (status != DirectNativeStatusV5.OK) return@synchronized statusFailure(path, status)
            val decoded = decodePathInfoV5(output)
            if (decoded == null || !decoded.path.sameFence(path)) invalidResponse(path)
            else DirectNativeResultV5.Success(decoded)
        }

    fun receiveEnvelope(
        path: DirectNativePathV5,
    ): DirectNativeResultV5<DirectNativeEnvelopeV5?> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        val probe = LongArray(READ_METADATA_VALUES)
        val probeStatus = call {
            methods.receiveEnvelopeV5(token, path.encode(), probe, ByteArray(0))
        } ?: return@synchronized ffiFailure(path)
        if (probeStatus == DirectNativeStatusV5.NO_DATA) {
            return@synchronized if (probe.all { it == 0L }) DirectNativeResultV5.Success(null)
            else invalidResponse(path)
        }
        if (probeStatus != DirectNativeStatusV5.BUFFER_TOO_SMALL || probe[0] != 0L ||
            probe[1] !in 1..DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5.toLong()
        ) return@synchronized statusOrInvalid(path, probeStatus)
        val output = ByteArray(probe[1].toInt())
        val metadata = LongArray(READ_METADATA_VALUES)
        val status = call { methods.receiveEnvelopeV5(token, path.encode(), metadata, output) }
            ?: return@synchronized ffiFailure(path)
        if (status != DirectNativeStatusV5.OK) {
            output.fill(0)
            return@synchronized statusFailure(path, status)
        }
        if (metadata[0] !in DIRECT_NATIVE_FIRST_DATA_FRAME_SEQUENCE_V5..Long.MAX_VALUE ||
            metadata[1] != probe[1]
        ) {
            output.fill(0)
            return@synchronized invalidResponse(path)
        }
        DirectNativeResultV5.Success(DirectNativeEnvelopeV5(metadata[0], output))
    }

    fun startProposal(value: DirectNativeProposalV5) =
        if (value.direction != DirectNativeDirectionV5.ANDROID_TO_PC) {
            invalidDirection(value.path)
        } else {
            recordOperation(value.path, value.encode(), methods::startProposalV5)
        }

    fun receiveProposal(value: DirectNativeProposalV5) =
        if (value.direction != DirectNativeDirectionV5.PC_TO_ANDROID) {
            invalidDirection(value.path)
        } else {
            recordOperation(value.path, value.encode(), methods::receiveProposalV5)
        }

    fun receiveAccept(value: DirectNativeTransitionV5) =
        if (value.direction != DirectNativeDirectionV5.ANDROID_TO_PC) {
            invalidDirection(value.path)
        } else {
            recordOperation(value.path, value.encode(), methods::receiveAcceptV5)
        }

    fun receiveCommit(value: DirectNativeTransitionV5) =
        if (value.direction == DirectNativeDirectionV5.PC_TO_ANDROID && !value.isEcho ||
            value.direction == DirectNativeDirectionV5.ANDROID_TO_PC && value.isEcho
        ) {
            recordOperation(value.path, value.encode(), methods::receiveCommitV5)
        } else {
            invalidDirection(value.path)
        }

    fun commitEchoSent(value: DirectNativeTransitionV5) =
        if (value.direction != DirectNativeDirectionV5.PC_TO_ANDROID) {
            invalidDirection(value.path)
        } else {
            recordOperation(value.path, value.encode(), methods::commitEchoSentV5)
        }

    fun receiveFallback(value: DirectNativeTransitionV5) =
        recordOperation(value.path, value.encode(), methods::receiveFallbackV5)

    fun tick(value: DirectNativeTransitionV5) =
        recordOperation(value.path, value.encode(), methods::tickV5)

    /** Must be called only after the existing renderer/durable-ACK commit succeeds. */
    fun updateAppliedCursor(value: DirectNativeCursorV5) =
        if (value.direction != DirectNativeDirectionV5.PC_TO_ANDROID) {
            invalidDirection(value.path)
        } else {
            recordOperation(value.path, value.encode(), methods::updateAppliedCursorV5)
        }

    /**
     * Removes one session from exactly [DirectNativeCursorV5.direction]. Callers close both
     * semantic directions independently; native makes repeated removal idempotent.
     */
    fun deregisterSession(value: DirectNativeCursorV5) =
        if (value.highestContiguous != 0L) {
            statusFailure(value.path, DirectNativeStatusV5.INVALID_ARGUMENT)
        } else {
            recordOperation(value.path, value.encode(), methods::deregisterSessionV5)
        }

    /** Native copies [exactEnvelope] before returning. */
    fun routeEnvelope(
        value: DirectNativeRouteEnvelopeV5,
        exactEnvelope: ByteArray,
    ): DirectNativeResultV5<Unit> = synchronized(lock) {
        val path = value.path
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        if (value.direction != DirectNativeDirectionV5.ANDROID_TO_PC) {
            return@synchronized invalidDirection(path)
        }
        if (exactEnvelope.isEmpty() || exactEnvelope.size > DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5) {
            return@synchronized statusFailure(path, DirectNativeStatusV5.INVALID_ARGUMENT)
        }
        val owned = exactEnvelope.copyOf()
        val status = try {
            call { methods.routeEnvelopeV5(token, value.encode(), owned) }
                ?: return@synchronized ffiFailure(path)
        } finally {
            owned.fill(0)
        }
        if (status == DirectNativeStatusV5.OK) DirectNativeResultV5.Success(Unit)
        else statusFailure(path, status)
    }

    fun pollRouteAction(path: DirectNativePathV5):
        DirectNativeResultV5<DirectNativeRouteActionV5?> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        repeat(DIRECT_NATIVE_MAX_RETAINED_FRAMES_V5) {
            when (val result = pollOneRouteAction(path)) {
                is DirectNativeResultV5.Failure -> return@synchronized result
                is DirectNativeResultV5.Success -> {
                    val action = result.value ?: run {
                        drainingPaths.clear()
                        return@synchronized result
                    }
                    if (action.path.sameFence(path)) return@synchronized result
                    if (drainingPaths.none(action.path::sameFence)) {
                        return@synchronized invalidResponse(path)
                    }
                    when (action.kind) {
                        // These carry exact bytes that must survive a path
                        // replacement. The platform consumes them under the
                        // predecessor's complete Direct carrier fence.
                        DirectNativeRouteActionKindV5.RELAY_ENVELOPE,
                        DirectNativeRouteActionKindV5.INGRESS_DELIVER,
                        DirectNativeRouteActionKindV5.FALLBACK,
                        -> return@synchronized result
                        DirectNativeRouteActionKindV5.CARRIER,
                        DirectNativeRouteActionKindV5.SESSION_CARRIER,
                        -> Unit
                        // Metadata-only work from a predecessor can no longer
                        // authorize a Direct write or ingress state transition.
                        DirectNativeRouteActionKindV5.DIRECT_ENVELOPE,
                        DirectNativeRouteActionKindV5.ACCEPT,
                        DirectNativeRouteActionKindV5.COMMIT,
                        DirectNativeRouteActionKindV5.INGRESS_HOLD,
                        DirectNativeRouteActionKindV5.INGRESS_DROP,
                        DirectNativeRouteActionKindV5.UNKNOWN,
                        -> Unit
                    }
                }
            }
        }
        invalidResponse(path)
    }

    private fun pollOneRouteAction(
        path: DirectNativePathV5,
    ): DirectNativeResultV5<DirectNativeRouteActionV5?> {
        val probeAction = ByteArray(ROUTE_ACTION_BYTES)
        val required = LongArray(1)
        val probeStatus = call {
            methods.pollRouteActionV5(token, probeAction, ByteArray(0), required)
        } ?: return ffiFailure(path)
        if (probeStatus == DirectNativeStatusV5.NO_DATA) {
            return if (probeAction.all { it == 0.toByte() } && required[0] == 0L) {
                DirectNativeResultV5.Success(null)
            } else invalidResponse(path)
        }
        if (probeStatus == DirectNativeStatusV5.OK) {
            val decoded = decodeRouteActionV5(probeAction, ByteArray(0))
            return if (required[0] == 0L && decoded != null) {
                DirectNativeResultV5.Success(decoded)
            } else invalidResponse(path)
        }
        if (probeStatus != DirectNativeStatusV5.BUFFER_TOO_SMALL ||
            probeAction.any { it != 0.toByte() } ||
            required[0] !in 1..DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5.toLong()
        ) return statusOrInvalid(path, probeStatus)
        val action = ByteArray(ROUTE_ACTION_BYTES)
        val payload = ByteArray(required[0].toInt())
        val copied = LongArray(1)
        val status = call { methods.pollRouteActionV5(token, action, payload, copied) }
            ?: return ffiFailure(path)
        if (status != DirectNativeStatusV5.OK) {
            payload.fill(0)
            return statusFailure(path, status)
        }
        val decoded = decodeRouteActionV5(action, payload)
        return if (copied[0] == required[0] && decoded != null) {
            DirectNativeResultV5.Success(decoded)
        } else {
            payload.fill(0)
            invalidResponse(path)
        }
    }

    fun arbitrateIngress(
        value: DirectNativeIngressV5,
        exactEnvelope: ByteArray,
    ): DirectNativeResultV5<Unit> = synchronized(lock) {
        val path = value.path
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        if (value.direction != DirectNativeDirectionV5.PC_TO_ANDROID) {
            return@synchronized invalidDirection(path)
        }
        if (exactEnvelope.isEmpty() || exactEnvelope.size > DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5) {
            return@synchronized statusFailure(path, DirectNativeStatusV5.INVALID_ARGUMENT)
        }
        val owned = exactEnvelope.copyOf()
        val status = try {
            call { methods.arbitrateIngressV5(token, value.encode(), owned) }
                ?: return@synchronized ffiFailure(path)
        } finally {
            owned.fill(0)
        }
        if (status == DirectNativeStatusV5.OK) DirectNativeResultV5.Success(Unit)
        else statusFailure(path, status)
    }

    fun tickIngress(path: DirectNativePathV5, nowMillis: Long): DirectNativeResultV5<Unit> =
        synchronized(lock) {
            if (closed) return@synchronized closedFailure(path)
            if (!owns(path)) return@synchronized staleFailure(path)
            if (nowMillis < 0) {
                return@synchronized statusFailure(path, DirectNativeStatusV5.INVALID_ARGUMENT)
            }
            val status = call { methods.tickIngressV5(token, path.encode(), nowMillis) }
                ?: return@synchronized ffiFailure(path)
            if (status == DirectNativeStatusV5.OK) DirectNativeResultV5.Success(Unit)
            else statusFailure(path, status)
        }

    fun pollEvent(): DirectNativeResultV5<DirectNativeEventV5?> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(currentPath)
        val output = ByteArray(EVENT_BYTES)
        val status = call { methods.pollEventV5(token, output) }
            ?: return@synchronized ffiFailure(currentPath)
        if (status == POLL_NO_EVENT) {
            return@synchronized if (output.all { it == 0.toByte() }) {
                DirectNativeResultV5.Success(null)
            } else invalidResponse(currentPath)
        }
        if (status != POLL_EVENT) return@synchronized statusFailure(currentPath, status)
        decodeEvent(output)
    }

    fun cancel(path: DirectNativePathV5): DirectNativeResultV5<Unit> =
        exactPathOperation(path) { methods.cancelV5(token, it) }

    fun networkChanged(path: DirectNativePathV5): DirectNativeResultV5<Unit> =
        exactPathOperation(path) { methods.networkChangedV5(token, it) }

    override fun close() {
        synchronized(lock) {
            if (closed) return
            closed = true
            pendingSigningRequests.clear()
            drainingPaths.clear()
            runCatching { methods.destroyV5(token) }
            currentPath = null
        }
    }

    private fun recordOperation(
        path: DirectNativePathV5,
        record: ByteArray,
        operation: (Long, ByteArray) -> Int,
    ): DirectNativeResultV5<Unit> = synchronized(lock) {
        try {
            if (closed) return@synchronized closedFailure(path)
            if (!owns(path)) return@synchronized staleFailure(path)
            val status = call { operation(token, record) } ?: return@synchronized ffiFailure(path)
            if (status == DirectNativeStatusV5.OK) DirectNativeResultV5.Success(Unit)
            else statusFailure(path, status)
        } finally {
            record.fill(0)
        }
    }

    private fun exactPathOperation(
        path: DirectNativePathV5,
        operation: (ByteArray) -> Int,
    ): DirectNativeResultV5<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(path)
        if (!owns(path)) return@synchronized staleFailure(path)
        val status = call { operation(path.encode()) } ?: return@synchronized ffiFailure(path)
        if (status == DirectNativeStatusV5.OK) DirectNativeResultV5.Success(Unit)
        else statusFailure(path, status)
    }

    private fun decodeEvent(bytes: ByteArray): DirectNativeResultV5<DirectNativeEventV5?> {
        val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        val kind = DirectNativeEventKindV5.from(buffer.getInt(8))
        val flags = buffer.getInt(16)
        val pathEpoch = buffer.getLong(24)
        val attemptId = bytes.copyOfRange(32, 48)
        val frameSequence = buffer.getLong(48)
        val payloadBytes = buffer.getInt(56)
        val releasedFrames = buffer.getInt(60)
        val status = buffer.getInt(12)
        if (buffer.getInt(0) != DIRECT_NATIVE_ABI_VERSION_V5 || buffer.getInt(4) != EVENT_BYTES ||
            kind == DirectNativeEventKindV5.UNKNOWN || buffer.getInt(20) != 0 || pathEpoch <= 0 ||
            !attemptId.isNonZeroIdentifierV5Local() || flags and KNOWN_EVENT_FLAGS.inv() != 0 ||
            payloadBytes !in 0..DIRECT_NATIVE_MAX_ENVELOPE_BYTES_V5 ||
            releasedFrames !in 0..DIRECT_NATIVE_MAX_FRAMES_V5 ||
            bytes.copyOfRange(64, 80).any { it != 0.toByte() }
        ) return invalidResponse(currentPath)
        val dataFrame = frameSequence in
            DIRECT_NATIVE_FIRST_DATA_FRAME_SEQUENCE_V5..DIRECT_NATIVE_MAX_FRAME_SEQUENCE_V5
        val noPayload = payloadBytes == 0
        val noRelease = releasedFrames == 0
        val validShape = when (kind) {
            DirectNativeEventKindV5.PATH_USED,
            DirectNativeEventKindV5.CANCELLED,
            DirectNativeEventKindV5.NETWORK_CHANGED ->
                status == DirectNativeStatusV5.OK && flags == 0 && frameSequence == 0L &&
                    noPayload && noRelease
            DirectNativeEventKindV5.ENVELOPE_QUEUED ->
                status == DirectNativeStatusV5.OK &&
                    flags == (EVENT_FLAG_OUTBOUND or EVENT_FLAG_TRANSPORT_COPY) && dataFrame &&
                    payloadBytes > 0 && noRelease
            DirectNativeEventKindV5.ENVELOPE_AVAILABLE ->
                status == DirectNativeStatusV5.OK && flags == EVENT_FLAG_INBOUND && dataFrame &&
                    payloadBytes > 0 && noRelease
            DirectNativeEventKindV5.TRANSPORT_RECEIPT ->
                status == DirectNativeStatusV5.OK && dataFrame && noPayload &&
                    (flags == EVENT_FLAG_OUTBOUND && noRelease ||
                        flags == (EVENT_FLAG_INBOUND or EVENT_FLAG_TRANSPORT_COPY))
            DirectNativeEventKindV5.QUEUE_LIMIT ->
                status == DirectNativeStatusV5.QUEUE_FULL && flags == 0 &&
                    frameSequence == 0L && noPayload && noRelease
            DirectNativeEventKindV5.PATH_READY ->
                status == DirectNativeStatusV5.OK &&
                    (flags == EVENT_FLAG_LAN || flags == EVENT_FLAG_NAT) &&
                    frameSequence == 0L && noPayload && noRelease
            DirectNativeEventKindV5.DIRECT_FAILED ->
                status in DirectNativeStatusV5.TICKET_EXPIRED..DirectNativeStatusV5.INVALID_HANDLE &&
                    flags == 0 && frameSequence == 0L && noPayload && noRelease
            DirectNativeEventKindV5.PING,
            DirectNativeEventKindV5.PONG ->
                status == DirectNativeStatusV5.OK &&
                    (flags == EVENT_FLAG_OUTBOUND || flags == EVENT_FLAG_INBOUND) && dataFrame &&
                    noPayload && noRelease
            DirectNativeEventKindV5.SIGN_REQUEST ->
                status == DirectNativeStatusV5.OK && flags == 0 && frameSequence > 0 &&
                    payloadBytes > 0 && noRelease
            DirectNativeEventKindV5.UNKNOWN -> false
        }
        if (!validShape) return invalidResponse(currentPath)
        val path = currentPath
        if (path != null && (pathEpoch != path.epoch || !attemptId.contentEquals(path.attemptId))) {
            return nativeFailureV5(
                DirectNativeSyntheticReasonV5.STALE_OWNER,
                pathEpoch,
                attemptId,
                status,
            )
        }
        return DirectNativeResultV5.Success(
            DirectNativeEventV5(
                kind, status, flags, pathEpoch, attemptId,
                frameSequence, payloadBytes, releasedFrames,
            ),
        )
    }

    private fun statusOrInvalid(
        path: DirectNativePathV5,
        status: Int,
    ): DirectNativeResultV5.Failure = if (status < 0 && status != DirectNativeStatusV5.BUFFER_TOO_SMALL) {
        statusFailure(path, status)
    } else {
        invalidResponse(path)
    }

    private fun owns(path: DirectNativePathV5) = currentPath?.sameFence(path) == true
    private inline fun call(operation: () -> Int): Int? = runCatching(operation).getOrNull()

    private fun ffiFailure(path: DirectNativePathV5?) = nativeFailureV5(
        DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
        path?.epoch ?: 0,
        path?.attemptId ?: ByteArray(16),
    )

    private fun invalidResponse(path: DirectNativePathV5?) = nativeFailureV5(
        DirectNativeSyntheticReasonV5.INVALID_NATIVE_RESPONSE,
        path?.epoch ?: 0,
        path?.attemptId ?: ByteArray(16),
    )

    private fun staleFailure(path: DirectNativePathV5) = nativeFailureV5(
        DirectNativeSyntheticReasonV5.STALE_OWNER,
        path.epoch,
        path.attemptId,
        DirectNativeStatusV5.STALE_PATH,
    )

    private fun invalidDirection(path: DirectNativePathV5) = nativeFailureV5(
        DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
        path.epoch,
        path.attemptId,
        DirectNativeStatusV5.INVALID_ARGUMENT,
    )

    private fun closedFailure(path: DirectNativePathV5?) = nativeFailureV5(
        DirectNativeSyntheticReasonV5.HANDLE_CLOSED,
        path?.epoch ?: 0,
        path?.attemptId ?: ByteArray(16),
        DirectNativeStatusV5.INVALID_HANDLE,
    )

    private fun statusFailure(path: DirectNativePathV5?, status: Int) = nativeFailureV5(
        DirectNativeSyntheticReasonV5.FFI_CALL_FAILED,
        path?.epoch ?: 0,
        path?.attemptId ?: ByteArray(16),
        status,
    )

    private companion object {
        const val READ_METADATA_VALUES = 2
        const val SIGNATURE_BYTES = 232
        const val PATH_INFO_BYTES = 128
        const val ROUTE_ACTION_BYTES = 200
        const val EVENT_BYTES = 80
        const val POLL_NO_EVENT = 0
        const val POLL_EVENT = 1
        const val EVENT_FLAG_OUTBOUND = 1
        const val EVENT_FLAG_INBOUND = 2
        const val EVENT_FLAG_TRANSPORT_COPY = 4
        const val EVENT_FLAG_LAN = 8
        const val EVENT_FLAG_NAT = 16
        const val KNOWN_EVENT_FLAGS = EVENT_FLAG_OUTBOUND or EVENT_FLAG_INBOUND or
            EVENT_FLAG_TRANSPORT_COPY or EVENT_FLAG_LAN or EVENT_FLAG_NAT
    }
}

internal data class DirectNativeEnvelopeV5(val frameSequence: Long, val bytes: ByteArray)

private val loadedMethodsV5: Result<DirectNativeMethodsV5> by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
    runCatching {
        val methods = JniDirectNativeMethodsV5
        check(DirectNativeAbiContractV5.from(methods.abiContractV5())?.isCompatible == true)
        methods
    }
}

private fun loadDirectNativeMethodsV5(): DirectNativeMethodsV5 = loadedMethodsV5.getOrThrow()

private fun nativeFailureV5(
    reason: Int,
    epoch: Long = 0,
    attemptId: ByteArray = ByteArray(16),
    nativeStatus: Int? = null,
) = DirectNativeResultV5.Failure(reason, nativeStatus, epoch, attemptId.copyOf())

private fun ByteBuffer.putBytes(offset: Int, value: ByteArray) {
    val previous = position()
    position(offset)
    put(value)
    position(previous)
}

private fun ByteArray.isNonZeroIdentifierV5Local() = size == 16 && any { it != 0.toByte() }

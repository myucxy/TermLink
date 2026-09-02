package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Pull-only ABI v4 boundary. It owns no Relay/WSS, session, renderer, Envelope,
 * durable ACK, reconnect, or routing API. A successful shadow path is diagnostic only.
 */
internal class JniDirectNetworkCoreV4(
    private val methodsProvider: () -> DirectNativeMethodsV4 = ::loadDirectNativeMethodsV4,
) {
    val directRoutingPermitted: Boolean get() = false

    fun abiContract(): DirectNativeResultV4<DirectNativeAbiContractV4> {
        val methods = runCatching(methodsProvider).getOrElse {
            return nativeFailureV4(DirectNativeSyntheticReasonV4.NATIVE_UNAVAILABLE)
        }
        val contract = runCatching {
            DirectNativeAbiContractV4.from(methods.abiContractV4())
        }.getOrNull()
        return if (contract?.isCompatible == true) DirectNativeResultV4.Success(contract)
        else nativeFailureV4(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
    }

    fun create(config: DirectNativeCreateConfigV4 = DirectNativeCreateConfigV4()):
        DirectNativeResultV4<DirectNativeHandleV4> {
        val methods = runCatching(methodsProvider).getOrElse {
            return nativeFailureV4(DirectNativeSyntheticReasonV4.NATIVE_UNAVAILABLE)
        }
        val contract = runCatching {
            DirectNativeAbiContractV4.from(methods.abiContractV4())
        }.getOrNull()
        if (contract?.isCompatible != true) {
            return nativeFailureV4(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
        }
        val output = LongArray(1)
        val status = runCatching { methods.createV4(config.enabled, config.queueCapacity, output) }
            .getOrElse { return nativeFailureV4(DirectNativeSyntheticReasonV4.FFI_CALL_FAILED) }
        return if (status == 0 && output[0] > 0) {
            DirectNativeResultV4.Success(DirectNativeHandleV4(methods, output[0]))
        } else {
            nativeFailureV4(DirectNativeSyntheticReasonV4.FFI_CALL_FAILED, nativeStatus = status)
        }
    }
}

internal class DirectNativeHandleV4(
    private val methods: DirectNativeMethodsV4,
    private val token: Long,
) : AutoCloseable {
    private val lock = Any()
    private var closed = false
    private var authoritativeEpoch = 0L
    private var authoritativeAttempt = ByteArray(16)
    private var authoritativeNetworkHandle = 0L
    private var lastEventGeneration = 0L
    private var lastCandidateBatch = 0
    private var totalCandidates = 0
    private var gatheringComplete = false
    private var checksStarted = false
    private var checkRoundId = ByteArray(16)
    private var lastPairBatch = 0
    private var totalPairs = 0
    private var planComplete = false
    private var nominationCommitted = false
    private val submittedPairs = LinkedHashMap<String, DirectNativeCandidatePairV4>()
    private val validatedPairs = LinkedHashSet<String>()
    private val pendingSignatures = LinkedHashSet<Long>()

    val directRoutingPermitted: Boolean get() = false

    fun gather(request: DirectNativeGatherRequestV4): DirectNativeResultV4<Unit> =
        synchronized(lock) {
            if (closed) return@synchronized closedFailure(request.epoch, request.attemptId)
            if (request.epoch <= authoritativeEpoch) {
                return@synchronized staleFailure(request.epoch, request.attemptId)
            }
            resetAttempt(request.epoch, request.attemptId, request.networkHandle)
            val scalars = longArrayOf(
                request.epoch,
                request.networkHandle,
                request.localBind.port.toLong(),
                request.networkCost.toLong(),
                request.candidateExpiresAtMillis,
                request.gatherTimeoutMillis.toLong(),
            )
            val blobs = buildList {
                add(request.attemptId.copyOf())
                add(request.localBind.ipAddress.copyOf())
                add(request.advertisedLocalIp.copyOf())
                request.stunServers.forEach { add(it.ipAddress.copyOf()) }
            }.toTypedArray()
            callCurrent(request.epoch, request.attemptId) {
                methods.gatherV4(
                    token,
                    scalars,
                    blobs,
                    request.stunServers.map { it.port }.toIntArray(),
                )
            }
        }

    fun beginChecks(request: DirectNativeBeginChecksRequestV4): DirectNativeResultV4<Unit> =
        synchronized(lock) {
            if (closed) return@synchronized closedFailure(request.epoch, request.attemptId)
            if (!isCurrent(request.epoch, request.attemptId)) {
                return@synchronized staleFailure(request.epoch, request.attemptId)
            }
            if (checksStarted || lastCandidateBatch == 0 ||
                request.networkHandle != authoritativeNetworkHandle
            ) return@synchronized planFailure(request.epoch, request.attemptId)
            val pairIdentities = request.pairs.map { it.identityKey() }
            if (pairIdentities.distinct().size != pairIdentities.size) {
                return@synchronized planFailure(request.epoch, request.attemptId)
            }
            val scalars = longArrayOf(
                request.epoch, request.networkHandle, request.nowMillis,
                request.timeoutMillis.toLong(), request.role.code.toLong(),
                request.pairBatchSequence.toLong(), request.totalPairCount.toLong(),
                if (request.planComplete) 1 else 0, request.pairs.size.toLong(),
            )
            val blobs = arrayOf(
                request.attemptId.copyOf(), request.connectionId.copyOf(),
                request.localDeviceId.copyOf(), request.peerDeviceId.copyOf(),
                request.ticket.copyOf(), request.serverSigningPublicKey.copyOf(),
                request.ticketId.copyOf(), request.checkRoundId.copyOf(),
                request.connectivityNonce.copyOf(), request.localSendProbeHmacKey.copyOf(),
                request.peerReceiveProbeHmacKey.copyOf(),
            )
            val result = try {
                callCurrent(request.epoch, request.attemptId) {
                    methods.beginChecksV4(
                        token, scalars, blobs,
                        request.pairs.map { it.encode() }.toTypedArray(),
                    )
                }
            } finally {
                blobs[9].fill(0)
                blobs[10].fill(0)
                request.destroySecrets()
            }
            if (result is DirectNativeResultV4.Success) {
                checksStarted = true
                checkRoundId = request.checkRoundId.copyOf()
                lastPairBatch = 1
                totalPairs = request.totalPairCount
                planComplete = request.planComplete
                request.pairs.forEach { submittedPairs[it.identityKey()] = it }
            }
            result
        }

    fun appendCheckPairs(request: DirectNativeAppendCheckPairsRequestV4):
        DirectNativeResultV4<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(request.epoch, request.attemptId)
        if (!isCurrent(request.epoch, request.attemptId)) {
            return@synchronized staleFailure(request.epoch, request.attemptId)
        }
        val keys = request.pairs.map { it.identityKey() }
        if (!checksStarted || planComplete ||
            !request.checkRoundId.contentEquals(checkRoundId) ||
            request.pairBatchSequence != lastPairBatch + 1 ||
            request.totalPairCount != totalPairs + request.pairs.size ||
            request.totalPairCount > DIRECT_NATIVE_MAX_PAIRS_TOTAL_V4 ||
            keys.distinct().size != keys.size || keys.any(submittedPairs::containsKey) ||
            request.pairs.map { it.rank } !=
                ((totalPairs + 1)..request.totalPairCount).toList()
        ) return@synchronized planFailure(request.epoch, request.attemptId)
        val result = callCurrent(request.epoch, request.attemptId) {
            methods.appendCheckPairsV4(
                token,
                longArrayOf(
                    request.epoch, request.pairBatchSequence.toLong(),
                    request.totalPairCount.toLong(), if (request.planComplete) 1 else 0,
                    request.pairs.size.toLong(),
                ),
                arrayOf(request.attemptId.copyOf(), request.checkRoundId.copyOf()),
                request.pairs.map { it.encode() }.toTypedArray(),
            )
        }
        if (result is DirectNativeResultV4.Success) {
            lastPairBatch = request.pairBatchSequence
            totalPairs = request.totalPairCount
            planComplete = request.planComplete
            request.pairs.forEach { submittedPairs[it.identityKey()] = it }
        }
        result
    }

    fun commitNomination(request: DirectNativeCommitNominationRequestV4):
        DirectNativeResultV4<Unit> = synchronized(lock) {
        if (closed) return@synchronized closedFailure(request.epoch, request.attemptId)
        if (!isCurrent(request.epoch, request.attemptId)) {
            return@synchronized staleFailure(request.epoch, request.attemptId)
        }
        val winnerKey = request.winningPair.identityKey()
        if (!checksStarted || nominationCommitted || !planComplete ||
            !request.checkRoundId.contentEquals(checkRoundId) ||
            winnerKey !in submittedPairs || winnerKey !in validatedPairs
        ) {
            return@synchronized nativeFailureV4(
                DirectNativeSyntheticReasonV4.NOMINATION_NOT_VALIDATED,
                request.epoch,
                request.attemptId,
            )
        }
        val result = callCurrent(request.epoch, request.attemptId) {
            methods.commitNominationV4(
                token,
                longArrayOf(
                    request.epoch, request.role.code.toLong(), request.timeoutMillis.toLong(),
                    1, request.nowMillis,
                ),
                arrayOf(
                    request.attemptId.copyOf(), request.checkRoundId.copyOf(),
                    request.nominationId.copyOf(), request.winningPair.encode(),
                    request.peerCertificateSha256.copyOf(),
                    request.peerDeviceSigningPublicKey.copyOf(),
                    request.localProofNonce.copyOf(),
                ),
            )
        }
        if (result is DirectNativeResultV4.Success) nominationCommitted = true
        result
    }

    fun cancel(epoch: Long, attemptId: ByteArray): DirectNativeResultV4<Unit> =
        exactControl(epoch, attemptId) { methods.cancelV4(token, epoch, it) }

    fun networkChanged(epoch: Long, attemptId: ByteArray): DirectNativeResultV4<Unit> =
        exactControl(epoch, attemptId) { methods.networkChangedV4(token, epoch, it) }

    fun submitSignature(
        epoch: Long,
        attemptId: ByteArray,
        requestId: Long,
        signature: ByteArray,
    ): DirectNativeResultV4<Unit> = synchronized(lock) {
        val ownedAttempt = attemptId.copyOf()
        val ownedSignature = signature.copyOf()
        if (closed) return@synchronized closedFailure(epoch, ownedAttempt)
        if (!isCurrent(epoch, ownedAttempt)) return@synchronized staleFailure(epoch, ownedAttempt)
        if (requestId !in pendingSignatures ||
            ownedSignature.isEmpty() || ownedSignature.size > DIRECT_NATIVE_MAX_SIGNATURE_BYTES_V4
        ) return@synchronized nativeFailureV4(
            DirectNativeSyntheticReasonV4.SIGNATURE_NOT_REQUESTED, epoch, ownedAttempt,
        )
        val result = callCurrent(epoch, ownedAttempt) {
            methods.submitSignatureV4(
                token, epoch, ownedAttempt.copyOf(), requestId, ownedSignature.copyOf(),
            )
        }
        if (result is DirectNativeResultV4.Success) pendingSignatures.remove(requestId)
        result
    }

    fun poll(): DirectNativeResultV4<DirectNativeEventV4?> = synchronized(lock) {
        if (closed) return@synchronized closedFailure()
        val metadata = LongArray(POLL_METADATA_VALUES)
        val attempt = ByteArray(16)
        val endpointIp = ByteArray(16)
        val payload = ByteArray(EVENT_UNION_BYTES)
        val status = runCatching {
            methods.pollV4(token, metadata, attempt, endpointIp, payload)
        }.getOrElse {
            return@synchronized retireFailure(DirectNativeSyntheticReasonV4.FFI_CALL_FAILED)
        }
        when (status) {
            0 -> DirectNativeResultV4.Success(null)
            1 -> decodeEvent(metadata, attempt, endpointIp, payload)
            else -> retireFailure(
                DirectNativeSyntheticReasonV4.FFI_CALL_FAILED,
                nativeStatus = status,
            )
        }
    }

    override fun close() = synchronized(lock) { retireLocked() }

    private fun exactControl(
        epoch: Long,
        attemptId: ByteArray,
        operation: (ByteArray) -> Int,
    ): DirectNativeResultV4<Unit> = synchronized(lock) {
        val ownedAttempt = attemptId.copyOf()
        if (closed) return@synchronized closedFailure(epoch, ownedAttempt)
        if (!isCurrent(epoch, ownedAttempt)) return@synchronized staleFailure(epoch, ownedAttempt)
        val status = runCatching { operation(ownedAttempt.copyOf()) }.getOrElse {
            retireLocked()
            return@synchronized nativeFailureV4(
                DirectNativeSyntheticReasonV4.FFI_CALL_FAILED, epoch, ownedAttempt,
            )
        }
        retireLocked()
        if (status == 0) DirectNativeResultV4.Success(Unit) else nativeFailureV4(
            DirectNativeSyntheticReasonV4.FFI_CALL_FAILED, epoch, ownedAttempt, status,
        )
    }

    private fun callCurrent(
        epoch: Long,
        attemptId: ByteArray,
        operation: () -> Int,
    ): DirectNativeResultV4<Unit> {
        val status = runCatching(operation).getOrElse {
            return retireFailure(DirectNativeSyntheticReasonV4.FFI_CALL_FAILED, epoch, attemptId)
        }
        return if (status == 0) DirectNativeResultV4.Success(Unit) else retireFailure(
            DirectNativeSyntheticReasonV4.FFI_CALL_FAILED, epoch, attemptId, status,
        )
    }

    private fun decodeEvent(
        metadata: LongArray,
        attemptId: ByteArray,
        endpointIp: ByteArray,
        payload: ByteArray,
    ): DirectNativeResultV4<DirectNativeEventV4?> {
        val kind = DirectNativeEventKindV4.from(metadata[2].toInt())
        val state = DirectNativePathStateV4.from(metadata[3].toInt())
        val payloadSize = metadata[5].toInt()
        val eventEpoch = metadata[6]
        val generation = metadata[7]
        val ipLength = metadata[9].toInt()
        val endpointPort = metadata[10].toInt()
        if (metadata[0].toInt() != DIRECT_NATIVE_ABI_VERSION_V4 || metadata[1] != 2_224L ||
            kind == DirectNativeEventKindV4.UNKNOWN || state == DirectNativePathStateV4.UNKNOWN ||
            lastEventGeneration == Long.MAX_VALUE || generation != lastEventGeneration + 1 ||
            payloadSize !in 0..EVENT_UNION_BYTES ||
            ipLength !in setOf(0, 4, 16) || endpointPort !in 0..65_535 ||
            (ipLength == 0) != (endpointPort == 0) ||
            (ipLength == 4 && endpointIp.drop(4).any { it != 0.toByte() }) ||
            !validEventPayload(kind, state, metadata[4].toInt(), metadata[8], payloadSize, payload) ||
            !validTypedEventOwner(kind, payload, eventEpoch, attemptId)
        ) return retireFailure(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
        lastEventGeneration = generation
        val endpoint = if (ipLength == 0) null else
            DirectNativeSocketAddressV4(endpointIp.copyOf(ipLength), endpointPort)
        if (kind != DirectNativeEventKindV4.IGNORED_STALE &&
            !isCurrent(eventEpoch, attemptId)
        ) {
            return DirectNativeResultV4.Success(
                DirectNativeEventV4(
                    DirectNativeEventKindV4.IGNORED_STALE,
                    DirectNativePathStateV4.RELAY_ONLY,
                    0,
                    authoritativeEpoch,
                    generation,
                    eventEpoch,
                    authoritativeAttempt.copyOf(),
                    null,
                    ByteArray(0),
                ),
            )
        }
        when (kind) {
            DirectNativeEventKindV4.CANDIDATE_BATCH -> if (!recordCandidateBatch(payload)) {
                return retireFailure(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
            }
            DirectNativeEventKindV4.PAIR_VALIDATED -> {
                val key = pairIdentityFromPayload(payload, 48)
                    ?: return retireFailure(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
                if (key !in submittedPairs) {
                    return retireFailure(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
                }
                validatedPairs += key
            }
            DirectNativeEventKindV4.SIGN_REQUEST -> pendingSignatures += metadata[8]
            DirectNativeEventKindV4.SHADOW_READY -> if (!nominationCommitted) {
                return retireFailure(DirectNativeSyntheticReasonV4.ABI_MISMATCH)
            }
            DirectNativeEventKindV4.DIRECT_FAILED -> retireLocked()
            else -> Unit
        }
        return DirectNativeResultV4.Success(
            DirectNativeEventV4(
                kind, state, metadata[4].toInt(), eventEpoch, generation, metadata[8],
                attemptId.copyOf(), endpoint, payload.copyOf(payloadSize),
            ),
        )
    }

    private fun validEventPayload(
        kind: DirectNativeEventKindV4,
        state: DirectNativePathStateV4,
        reason: Int,
        requestId: Long,
        payloadSize: Int,
        payload: ByteArray,
    ): Boolean {
        val expected = when (kind) {
            DirectNativeEventKindV4.IDENTITY_READY,
            DirectNativeEventKindV4.SIGN_REQUEST,
            -> 2_080
            DirectNativeEventKindV4.CANDIDATE_BATCH -> 1_088
            DirectNativeEventKindV4.PAIR_VALIDATED -> 360
            DirectNativeEventKindV4.CHECKS_COMPLETE -> 80
            DirectNativeEventKindV4.NOMINATION_READY -> 376
            DirectNativeEventKindV4.SHADOW_READY -> 400
            DirectNativeEventKindV4.STATE_CHANGED,
            DirectNativeEventKindV4.DIRECT_FAILED,
            DirectNativeEventKindV4.IGNORED_STALE,
            -> 0
            else -> return false
        }
        if (payloadSize != expected) return false
        if (expected > 0) {
            val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
            if (buffer.getInt(0) != DIRECT_NATIVE_ABI_VERSION_V4 || buffer.getInt(4) != expected) {
                return false
            }
        }
        return when (kind) {
            DirectNativeEventKindV4.IDENTITY_READY ->
                state == DirectNativePathStateV4.GATHERING && reason == 0 && requestId == 0L
            DirectNativeEventKindV4.STATE_CHANGED -> reason == 0 && requestId == 0L &&
                state in setOf(
                    DirectNativePathStateV4.GATHERING, DirectNativePathStateV4.CHECKING,
                    DirectNativePathStateV4.NOMINATION_PENDING,
                    DirectNativePathStateV4.AUTHENTICATING,
                )
            DirectNativeEventKindV4.DIRECT_FAILED ->
                state == DirectNativePathStateV4.RELAY_ONLY && reason in 1..17 && requestId == 0L
            DirectNativeEventKindV4.SIGN_REQUEST ->
                state == DirectNativePathStateV4.AUTHENTICATING && reason == 0 && requestId > 0
            DirectNativeEventKindV4.CANDIDATE_BATCH ->
                state == DirectNativePathStateV4.GATHERING && reason == 0 && requestId == 0L
            DirectNativeEventKindV4.PAIR_VALIDATED ->
                state == DirectNativePathStateV4.CHECKING && reason == 0 && requestId == 0L
            DirectNativeEventKindV4.CHECKS_COMPLETE ->
                state in setOf(
                    DirectNativePathStateV4.CHECKING,
                    DirectNativePathStateV4.NOMINATION_PENDING,
                ) && reason == 0 && requestId == 0L
            DirectNativeEventKindV4.NOMINATION_READY ->
                state == DirectNativePathStateV4.NOMINATION_PENDING && reason == 0 && requestId == 0L
            DirectNativeEventKindV4.SHADOW_READY ->
                state == DirectNativePathStateV4.SHADOW_DIRECT && reason == 0 && requestId == 0L
            DirectNativeEventKindV4.IGNORED_STALE ->
                state == DirectNativePathStateV4.RELAY_ONLY && reason == 0 && requestId > 0
            else -> false
        }
    }

    private fun validTypedEventOwner(
        kind: DirectNativeEventKindV4,
        payload: ByteArray,
        epoch: Long,
        attemptId: ByteArray,
    ): Boolean {
        val ownsAttempt = kind in setOf(
            DirectNativeEventKindV4.CANDIDATE_BATCH,
            DirectNativeEventKindV4.PAIR_VALIDATED,
            DirectNativeEventKindV4.CHECKS_COMPLETE,
            DirectNativeEventKindV4.NOMINATION_READY,
            DirectNativeEventKindV4.SHADOW_READY,
        )
        if (!ownsAttempt) return true
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        if (buffer.getLong(8) != epoch ||
            !payload.copyOfRange(16, 32).contentEquals(attemptId)
        ) return false
        if (kind != DirectNativeEventKindV4.CANDIDATE_BATCH && checksStarted &&
            !payload.copyOfRange(32, 48).contentEquals(checkRoundId)
        ) return false
        return true
    }

    private fun recordCandidateBatch(payload: ByteArray): Boolean {
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        val batch = buffer.getInt(32)
        val count = buffer.getInt(36)
        val total = buffer.getInt(40)
        val complete = buffer.getInt(44)
        if (batch != lastCandidateBatch + 1 || count !in 0..8 ||
            total != totalCandidates + count || total !in 0..32 || complete !in 0..1 ||
            (count == 0 && complete == 0) || gatheringComplete
        ) return false
        lastCandidateBatch = batch
        totalCandidates = total
        gatheringComplete = complete == 1
        return true
    }

    private fun pairIdentityFromPayload(payload: ByteArray, pairOffset: Int): String? {
        if (payload.size < pairOffset + 288) return null
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        if (buffer.getInt(pairOffset) != 4 || buffer.getInt(pairOffset + 4) != 288) return null
        val rank = buffer.getInt(pairOffset + 8)
        val first = payload.copyOfRange(pairOffset + 24, pairOffset + 40)
        val second = payload.copyOfRange(pairOffset + 152, pairOffset + 168)
        return identityKey(rank, first, second)
    }

    private fun DirectNativeCandidatePairV4.identityKey(): String =
        identityKey(rank, initiator.candidateId, responder.candidateId)

    private fun identityKey(rank: Int, first: ByteArray, second: ByteArray): String =
        buildString(72) {
            append(rank).append(':')
            first.forEach { append(HEX[(it.toInt() ushr 4) and 15]).append(HEX[it.toInt() and 15]) }
            append(':')
            second.forEach { append(HEX[(it.toInt() ushr 4) and 15]).append(HEX[it.toInt() and 15]) }
        }

    private fun resetAttempt(epoch: Long, attempt: ByteArray, networkHandle: Long) {
        authoritativeEpoch = epoch
        authoritativeAttempt = attempt.copyOf()
        authoritativeNetworkHandle = networkHandle
        lastCandidateBatch = 0
        totalCandidates = 0
        gatheringComplete = false
        checksStarted = false
        checkRoundId = ByteArray(16)
        lastPairBatch = 0
        totalPairs = 0
        planComplete = false
        nominationCommitted = false
        submittedPairs.clear()
        validatedPairs.clear()
        pendingSignatures.clear()
    }

    private fun isCurrent(epoch: Long, attemptId: ByteArray): Boolean =
        epoch == authoritativeEpoch && attemptId.contentEquals(authoritativeAttempt)

    private fun planFailure(epoch: Long, attemptId: ByteArray): DirectNativeResultV4<Unit> =
        nativeFailureV4(DirectNativeSyntheticReasonV4.INVALID_PLAN, epoch, attemptId)

    private fun retireLocked() {
        if (closed) return
        closed = true
        submittedPairs.clear()
        validatedPairs.clear()
        pendingSignatures.clear()
        runCatching { methods.destroyV4(token) }
    }

    private fun <T> retireFailure(
        reason: Int,
        epoch: Long = authoritativeEpoch,
        attemptId: ByteArray = authoritativeAttempt,
        nativeStatus: Int? = null,
    ): DirectNativeResultV4<T> {
        retireLocked()
        return nativeFailureV4(reason, epoch, attemptId, nativeStatus)
    }

    private fun <T> staleFailure(epoch: Long, attemptId: ByteArray): DirectNativeResultV4<T> =
        nativeFailureV4(DirectNativeSyntheticReasonV4.STALE_OWNER, epoch, attemptId)

    private fun <T> closedFailure(
        epoch: Long = authoritativeEpoch,
        attemptId: ByteArray = authoritativeAttempt,
    ): DirectNativeResultV4<T> =
        nativeFailureV4(DirectNativeSyntheticReasonV4.HANDLE_CLOSED, epoch, attemptId)

    private companion object {
        const val POLL_METADATA_VALUES = 11
        const val EVENT_UNION_BYTES = 2_080
        const val HEX = "0123456789abcdef"
    }
}

private val loadedMethodsV4: Result<DirectNativeMethodsV4> by lazy(LazyThreadSafetyMode.SYNCHRONIZED) {
    runCatching {
        val methods = JniDirectNativeMethodsV4
        check(DirectNativeAbiContractV4.from(methods.abiContractV4())?.isCompatible == true)
        methods
    }
}

private fun loadDirectNativeMethodsV4(): DirectNativeMethodsV4 = loadedMethodsV4.getOrThrow()

private fun <T> nativeFailureV4(
    reason: Int,
    epoch: Long = 0,
    attemptId: ByteArray = ByteArray(16),
    nativeStatus: Int? = null,
): DirectNativeResultV4<T> = DirectNativeResultV4.Failure(
    DirectNativeEventV4.relayOnly(reason, epoch, attemptId), nativeStatus,
)

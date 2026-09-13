package dev.termlink.network.direct.nativebridge

import java.nio.ByteBuffer
import java.nio.ByteOrder

internal const val DIRECT_NATIVE_ABI_VERSION_V4 = 4
internal const val DIRECT_NATIVE_PLATFORM_ANDROID_V4 = 2
internal const val DIRECT_NATIVE_MAX_STUN_SERVERS_V4 = 2
internal const val DIRECT_NATIVE_MAX_CANDIDATES_PER_BATCH_V4 = 8
internal const val DIRECT_NATIVE_MAX_CANDIDATES_TOTAL_V4 = 32
internal const val DIRECT_NATIVE_MAX_PAIRS_PER_BATCH_V4 = 8
internal const val DIRECT_NATIVE_MAX_PAIRS_TOTAL_V4 = 32
internal const val DIRECT_NATIVE_MAX_EVENT_BYTES_V4 = 2_048
internal const val DIRECT_NATIVE_MAX_SIGNATURE_BYTES_V4 = 128

/** Runtime proof of the independent, frozen `termlink_direct_ffi_v4.h` layout. */
internal class DirectNativeAbiContractV4 private constructor(raw: LongArray) {
    private val values = raw.copyOf()
    val abiVersion: Int get() = values[0].toInt()

    val isCompatible: Boolean
        get() = values.contentEquals(EXPECTED)

    companion object {
        private val EXPECTED = longArrayOf(
            4, 32, 48, 256, 128, 1_088, 288, 4_656, 2_400, 504,
            48, 192, 2_080, 360, 80, 376, 400, 2_080, 2_224,
            40, 128, 40, 48, 16, 144, 128, 2_256, 2_320, 64, 64,
            432, 32, 64, 112, 2_192, 4_624,
        )

        fun from(raw: LongArray): DirectNativeAbiContractV4? =
            raw.takeIf { it.size == EXPECTED.size }?.let(::DirectNativeAbiContractV4)
    }
}

internal data class DirectNativeCreateConfigV4(
    val enabled: Boolean = false,
    val queueCapacity: Int = 16,
) {
    init {
        require(queueCapacity in 1..64) { "Direct v4 native queue capacity must be in 1..64" }
    }
}

internal class DirectNativeSocketAddressV4(ipAddress: ByteArray, val port: Int) {
    val ipAddress = ipAddress.copyOf()

    init {
        require(this.ipAddress.size == 4 || this.ipAddress.size == 16)
        require(port in 0..65_535)
    }

    internal fun ownedCopy() = DirectNativeSocketAddressV4(ipAddress, port)

    override fun equals(other: Any?): Boolean = other is DirectNativeSocketAddressV4 &&
        port == other.port && ipAddress.contentEquals(other.ipAddress)

    override fun hashCode(): Int = 31 * ipAddress.contentHashCode() + port
}

internal enum class DirectNativeCandidateKindV4(val code: Int) {
    HOST(1), SERVER_REFLEXIVE(2);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code }
    }
}

internal enum class DirectNativeRoleV4(val code: Int) { INITIATOR(1), RESPONDER(2) }

internal class DirectNativeCandidateV4(
    candidateId: ByteArray,
    relatedCandidateId: ByteArray,
    endpoint: DirectNativeSocketAddressV4,
    val kind: DirectNativeCandidateKindV4,
    val priority: Int,
    val networkCost: Int,
    val expiresAtMillis: Long,
) {
    val candidateId = candidateId.copyOf()
    val relatedCandidateId = relatedCandidateId.copyOf()
    val endpoint = endpoint.ownedCopy()

    init {
        require(this.candidateId.isNonZeroIdentifierV4())
        require(this.relatedCandidateId.size == 16)
        require(this.endpoint.port in 1_024..65_535)
        require(priority > 0 && networkCost >= 0 && expiresAtMillis > 0)
        require(
            (kind == DirectNativeCandidateKindV4.HOST &&
                this.relatedCandidateId.all { it == 0.toByte() }) ||
                (kind == DirectNativeCandidateKindV4.SERVER_REFLEXIVE &&
                    this.relatedCandidateId.isNonZeroIdentifierV4()),
        )
    }

    internal fun encode(): ByteArray = ByteBuffer.allocate(CANDIDATE_WIRE_BYTES)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(candidateId)
        .put(relatedCandidateId)
        .put(endpoint.ipAddress.copyOf(16))
        .putInt(endpoint.ipAddress.size)
        .putInt(endpoint.port)
        .putInt(kind.code)
        .putInt(1) // UDP/QUIC is the only v4 carrier.
        .putInt(priority)
        .putInt(networkCost)
        .putLong(expiresAtMillis)
        .array()

    internal fun ownedCopy() = DirectNativeCandidateV4(
        candidateId,
        relatedCandidateId,
        endpoint,
        kind,
        priority,
        networkCost,
        expiresAtMillis,
    )

    internal fun destroy() {
        candidateId.fill(0)
        relatedCandidateId.fill(0)
        endpoint.ipAddress.fill(0)
    }

    companion object {
        internal const val CANDIDATE_WIRE_BYTES = 80
    }
}

internal class DirectNativeCandidatePairV4(
    val rank: Int,
    initiator: DirectNativeCandidateV4,
    responder: DirectNativeCandidateV4,
) {
    val initiator = initiator.ownedCopy()
    val responder = responder.ownedCopy()

    init {
        require(rank in 1..DIRECT_NATIVE_MAX_PAIRS_TOTAL_V4)
        require(!initiator.candidateId.contentEquals(responder.candidateId))
        require(initiator.endpoint.ipAddress.size == responder.endpoint.ipAddress.size)
    }

    internal fun encode(): ByteArray = ByteBuffer.allocate(PAIR_WIRE_BYTES)
        .order(ByteOrder.LITTLE_ENDIAN)
        .putInt(rank)
        .put(initiator.encode())
        .put(responder.encode())
        .array()

    internal fun identity(): ByteArray = ByteBuffer.allocate(36)
        .order(ByteOrder.LITTLE_ENDIAN)
        .putInt(rank)
        .put(initiator.candidateId)
        .put(responder.candidateId)
        .array()

    internal fun ownedCopy() = DirectNativeCandidatePairV4(rank, initiator, responder)

    internal fun destroy() {
        initiator.destroy()
        responder.destroy()
    }

    companion object {
        internal const val PAIR_WIRE_BYTES = 164
    }
}

internal class DirectNativeGatherRequestV4(
    val epoch: Long,
    attemptId: ByteArray,
    val networkHandle: Long,
    localBind: DirectNativeSocketAddressV4,
    advertisedLocalIp: ByteArray,
    val networkCost: Int,
    val candidateExpiresAtMillis: Long,
    stunServers: List<DirectNativeSocketAddressV4>,
    val gatherTimeoutMillis: Int,
) {
    val attemptId = attemptId.copyOf()
    val localBind = localBind.ownedCopy()
    val advertisedLocalIp = advertisedLocalIp.copyOf()
    val stunServers = stunServers.map { it.ownedCopy() }

    init {
        require(epoch > 0 && this.attemptId.isNonZeroIdentifierV4())
        require(networkHandle != 0L)
        require(this.localBind.port == 0 || this.localBind.port >= 1_024)
        require(this.advertisedLocalIp.size in setOf(4, 16))
        require(this.advertisedLocalIp.size == this.localBind.ipAddress.size)
        require(networkCost >= 0 && candidateExpiresAtMillis > 0)
        require(this.stunServers.size <= DIRECT_NATIVE_MAX_STUN_SERVERS_V4)
        require(this.stunServers.all { it.port in 1_024..65_535 })
        require(gatherTimeoutMillis in 100..5_000)
    }
}

internal class DirectNativeBeginChecksRequestV4(
    val epoch: Long,
    attemptId: ByteArray,
    connectionId: ByteArray,
    localDeviceId: ByteArray,
    peerDeviceId: ByteArray,
    val networkHandle: Long,
    val nowMillis: Long,
    val timeoutMillis: Int,
    val role: DirectNativeRoleV4,
    ticket: ByteArray,
    val pairBatchSequence: Int,
    val totalPairCount: Int,
    val planComplete: Boolean,
    serverSigningPublicKey: ByteArray,
    ticketId: ByteArray,
    checkRoundId: ByteArray,
    connectivityNonce: ByteArray,
    localSendProbeHmacKey: ByteArray,
    peerReceiveProbeHmacKey: ByteArray,
    pairs: List<DirectNativeCandidatePairV4>,
) {
    val attemptId = attemptId.copyOf()
    val connectionId = connectionId.copyOf()
    val localDeviceId = localDeviceId.copyOf()
    val peerDeviceId = peerDeviceId.copyOf()
    val ticket = ticket.copyOf()
    val serverSigningPublicKey = serverSigningPublicKey.copyOf()
    val ticketId = ticketId.copyOf()
    val checkRoundId = checkRoundId.copyOf()
    val connectivityNonce = connectivityNonce.copyOf()
    val localSendProbeHmacKey = localSendProbeHmacKey.copyOf()
    val peerReceiveProbeHmacKey = peerReceiveProbeHmacKey.copyOf()
    val pairs = pairs.map(DirectNativeCandidatePairV4::ownedCopy)

    init {
        require(epoch > 0 && networkHandle != 0L && nowMillis > 0)
        listOf(this.attemptId, this.connectionId, this.localDeviceId, this.peerDeviceId,
            this.ticketId, this.checkRoundId, this.connectivityNonce).forEach {
            require(it.isNonZeroIdentifierV4())
        }
        require(!this.localDeviceId.contentEquals(this.peerDeviceId))
        require(timeoutMillis in 500..10_000)
        require(this.ticket.isNotEmpty() && this.ticket.size <= 2_048)
        require(this.serverSigningPublicKey.size == 32)
        require(this.localSendProbeHmacKey.size == 32 && this.peerReceiveProbeHmacKey.size == 32)
        require(!this.localSendProbeHmacKey.contentEquals(this.peerReceiveProbeHmacKey))
        require(pairBatchSequence == 1)
        require(this.pairs.size in 1..DIRECT_NATIVE_MAX_PAIRS_PER_BATCH_V4)
        require(totalPairCount == this.pairs.size)
        require(this.pairs.map { it.rank }.distinct().size == this.pairs.size)
        require(this.pairs.map { it.rank } == (1..this.pairs.size).toList())
    }

    internal fun destroySecrets() {
        localSendProbeHmacKey.fill(0)
        peerReceiveProbeHmacKey.fill(0)
    }
}

internal class DirectNativeAppendCheckPairsRequestV4(
    val epoch: Long,
    attemptId: ByteArray,
    checkRoundId: ByteArray,
    val pairBatchSequence: Int,
    val totalPairCount: Int,
    val planComplete: Boolean,
    pairs: List<DirectNativeCandidatePairV4>,
) {
    val attemptId = attemptId.copyOf()
    val checkRoundId = checkRoundId.copyOf()
    val pairs = pairs.map(DirectNativeCandidatePairV4::ownedCopy)

    init {
        require(epoch > 0 && this.attemptId.isNonZeroIdentifierV4() &&
            this.checkRoundId.isNonZeroIdentifierV4())
        require(pairBatchSequence > 1)
        require(this.pairs.size <= DIRECT_NATIVE_MAX_PAIRS_PER_BATCH_V4)
        require(this.pairs.isNotEmpty() || planComplete)
        require(totalPairCount in this.pairs.size..DIRECT_NATIVE_MAX_PAIRS_TOTAL_V4)
        require(this.pairs.map { it.rank }.distinct().size == this.pairs.size)
    }
}

internal class DirectNativeCommitNominationRequestV4(
    val epoch: Long,
    attemptId: ByteArray,
    checkRoundId: ByteArray,
    nominationId: ByteArray,
    winningPair: DirectNativeCandidatePairV4,
    val role: DirectNativeRoleV4,
    val timeoutMillis: Int,
    val selectionConfirmed: Boolean,
    peerCertificateSha256: ByteArray,
    peerDeviceSigningPublicKey: ByteArray,
    localProofNonce: ByteArray,
    val nowMillis: Long,
) {
    val attemptId = attemptId.copyOf()
    val checkRoundId = checkRoundId.copyOf()
    val nominationId = nominationId.copyOf()
    val winningPair = winningPair.ownedCopy()
    val peerCertificateSha256 = peerCertificateSha256.copyOf()
    val peerDeviceSigningPublicKey = peerDeviceSigningPublicKey.copyOf()
    val localProofNonce = localProofNonce.copyOf()

    init {
        require(epoch > 0 && nowMillis > 0)
        listOf(this.attemptId, this.checkRoundId, this.nominationId).forEach {
            require(it.isNonZeroIdentifierV4())
        }
        require(timeoutMillis in 500..10_000)
        require(selectionConfirmed) { "Unauthenticated v4 nomination cannot be committed" }
        require(this.peerCertificateSha256.size == 32 &&
            this.peerDeviceSigningPublicKey.size == 32)
        require(this.localProofNonce.size in 16..32)
    }

    internal fun destroy() {
        attemptId.fill(0)
        checkRoundId.fill(0)
        nominationId.fill(0)
        winningPair.destroy()
        peerCertificateSha256.fill(0)
        peerDeviceSigningPublicKey.fill(0)
        localProofNonce.fill(0)
    }
}

internal enum class DirectNativeEventKindV4(val code: Int) {
    IDENTITY_READY(1), STATE_CHANGED(2), DIRECT_FAILED(3), SIGN_REQUEST(4),
    CANDIDATE_BATCH(5), PAIR_VALIDATED(6), CHECKS_COMPLETE(7), NOMINATION_READY(8),
    SHADOW_READY(9), IGNORED_STALE(10), SYNTHETIC_FAILURE(1_000), UNKNOWN(-1);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

internal enum class DirectNativePathStateV4(val code: Int) {
    RELAY_ONLY(1), GATHERING(2), CHECKING(3), NOMINATION_PENDING(4),
    AUTHENTICATING(5), SHADOW_DIRECT(6), UNKNOWN(-1);

    companion object {
        fun from(code: Int) = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

internal data class DirectNativeEventV4(
    val kind: DirectNativeEventKindV4,
    val state: DirectNativePathStateV4,
    val reason: Int,
    val epoch: Long,
    val generation: Long,
    val requestId: Long,
    val attemptId: ByteArray,
    val endpoint: DirectNativeSocketAddressV4?,
    val payload: ByteArray,
) {
    companion object {
        fun relayOnly(reason: Int, epoch: Long = 0, attemptId: ByteArray = ByteArray(16)) =
            DirectNativeEventV4(
                DirectNativeEventKindV4.SYNTHETIC_FAILURE,
                DirectNativePathStateV4.RELAY_ONLY,
                reason, epoch, 0, 0, attemptId.copyOf(), null, ByteArray(0),
            )
    }
}

internal sealed interface DirectNativeResultV4<out T> {
    data class Success<T>(val value: T) : DirectNativeResultV4<T>
    data class Failure(val relayOnly: DirectNativeEventV4, val nativeStatus: Int? = null) :
        DirectNativeResultV4<Nothing>
}

internal object DirectNativeSyntheticReasonV4 {
    const val NATIVE_UNAVAILABLE = 1_001
    const val ABI_MISMATCH = 1_002
    const val FFI_CALL_FAILED = 1_003
    const val HANDLE_CLOSED = 1_004
    const val STALE_OWNER = 1_005
    const val INVALID_PLAN = 1_006
    const val SIGNATURE_NOT_REQUESTED = 1_007
    const val NOMINATION_NOT_VALIDATED = 1_008
}

private fun ByteArray.isNonZeroIdentifierV4(): Boolean = size == 16 && any { it != 0.toByte() }

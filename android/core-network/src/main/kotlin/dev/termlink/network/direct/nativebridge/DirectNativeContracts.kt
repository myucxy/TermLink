package dev.termlink.network.direct.nativebridge

internal const val DIRECT_NATIVE_ABI_VERSION = 3
internal const val DIRECT_NATIVE_PLATFORM_ANDROID = 2
internal const val DIRECT_NATIVE_MAX_STUN_SERVERS = 2
internal const val DIRECT_NATIVE_MAX_CANDIDATES = 3
internal const val DIRECT_NATIVE_CANDIDATES_BYTES = 80
internal const val DIRECT_NATIVE_MAX_EVENT_PAYLOAD_BYTES = 2_048
internal const val DIRECT_NATIVE_MAX_SIGNATURE_BYTES = 128

/** Runtime sizeof/offsetof proof for the frozen `termlink_direct_ffi_v3.h` layout. */
internal data class DirectNativeAbiContract(
    val abiVersion: Int,
    val createSize: Int,
    val socketAddressSize: Int,
    val gatherSize: Int,
    val nominateSize: Int,
    val candidatesSize: Int,
    val controlSize: Int,
    val signatureSize: Int,
    val eventSize: Int,
    val gatherLocalBindOffset: Int,
    val gatherStunServersOffset: Int,
    val gatherReservedOffset: Int,
    val nominateStartV2Offset: Int,
    val nominateLocalSendProbeKeyOffset: Int,
    val nominatePeerReceiveProbeKeyOffset: Int,
    val nominateReservedOffset: Int,
    val candidatesArrayOffset: Int,
    val startTicketOffset: Int,
    val startPeerCertificateSha256Offset: Int,
    val startNowMillisOffset: Int,
    val eventGenerationOffset: Int,
    val eventPayloadOffset: Int,
) {
    val isCompatible: Boolean
        get() = abiVersion == DIRECT_NATIVE_ABI_VERSION &&
            createSize == 32 && socketAddressSize == 24 && gatherSize == 176 &&
            nominateSize == 2_696 && candidatesSize == DIRECT_NATIVE_CANDIDATES_BYTES &&
            controlSize == 32 && signatureSize == 176 && eventSize == 2_136 &&
            gatherLocalBindOffset == 40 && gatherStunServersOffset == 88 &&
            gatherReservedOffset == 144 && nominateStartV2Offset == 8 &&
            nominateLocalSendProbeKeyOffset == 2_600 &&
            nominatePeerReceiveProbeKeyOffset == 2_632 && nominateReservedOffset == 2_664 &&
            candidatesArrayOffset == 8 && startTicketOffset == 208 &&
            startPeerCertificateSha256Offset == 2_288 && startNowMillisOffset == 2_552 &&
            eventGenerationOffset == 32 && eventPayloadOffset == 88

    companion object {
        fun from(raw: LongArray): DirectNativeAbiContract? = raw.takeIf { it.size == 22 }?.let {
            DirectNativeAbiContract(
                abiVersion = it[0].toInt(), createSize = it[1].toInt(),
                socketAddressSize = it[2].toInt(), gatherSize = it[3].toInt(),
                nominateSize = it[4].toInt(), candidatesSize = it[5].toInt(),
                controlSize = it[6].toInt(), signatureSize = it[7].toInt(),
                eventSize = it[8].toInt(), gatherLocalBindOffset = it[9].toInt(),
                gatherStunServersOffset = it[10].toInt(), gatherReservedOffset = it[11].toInt(),
                nominateStartV2Offset = it[12].toInt(),
                nominateLocalSendProbeKeyOffset = it[13].toInt(),
                nominatePeerReceiveProbeKeyOffset = it[14].toInt(),
                nominateReservedOffset = it[15].toInt(), candidatesArrayOffset = it[16].toInt(),
                startTicketOffset = it[17].toInt(),
                startPeerCertificateSha256Offset = it[18].toInt(),
                startNowMillisOffset = it[19].toInt(), eventGenerationOffset = it[20].toInt(),
                eventPayloadOffset = it[21].toInt(),
            )
        }
    }
}

internal data class DirectNativeCreateConfig(
    val enabled: Boolean = false,
    val queueCapacity: Int = 8,
) {
    init {
        require(queueCapacity in 1..64) { "direct native queue capacity must be in 1..64" }
    }
}

/** Copy-in endpoint value. IP storage is never shared with its constructor caller. */
internal class DirectNativeSocketAddress(ipAddress: ByteArray, val port: Int) {
    val ipAddress: ByteArray = ipAddress.copyOf()

    init {
        require(this.ipAddress.size == 4 || this.ipAddress.size == 16) {
            "direct socket address must contain a network-order IPv4 or IPv6 address"
        }
        require(port in 0..65_535) { "direct socket port is outside the unsigned 16-bit range" }
    }

    internal fun ownedCopy() = DirectNativeSocketAddress(ipAddress, port)

    override fun equals(other: Any?): Boolean =
        other is DirectNativeSocketAddress && port == other.port &&
            ipAddress.contentEquals(other.ipAddress)

    override fun hashCode(): Int = 31 * ipAddress.contentHashCode() + port
}

internal enum class DirectNativeRole(val code: Int) {
    INITIATOR(1),
    RESPONDER(2),
}

/** Fully owned input for V3 Gather. The Android network handle must be non-zero. */
internal class DirectNativeGatherRequest(
    val epoch: Long,
    attemptId: ByteArray,
    val networkHandle: Long,
    localBind: DirectNativeSocketAddress,
    advertisedLocalIp: ByteArray,
    stunServers: List<DirectNativeSocketAddress>,
    val gatherTimeoutMillis: Int,
) {
    val attemptId: ByteArray = attemptId.copyOf()
    val localBind: DirectNativeSocketAddress = localBind.ownedCopy()
    val advertisedLocalIp: ByteArray = advertisedLocalIp.copyOf()
    val stunServers: List<DirectNativeSocketAddress> = stunServers.map { it.ownedCopy() }

    init {
        require(epoch in 1..Long.MAX_VALUE) { "direct path epoch must be positive" }
        require(this.attemptId.isNonZeroIdentifier()) { "direct attempt id must be a nonzero 16-byte value" }
        require(networkHandle != 0L) { "Android Network.getNetworkHandle() is required" }
        require(this.localBind.port == 0 || this.localBind.port >= 1_024) {
            "direct gather bind must use port zero or a high port"
        }
        require(this.advertisedLocalIp.size == 4 || this.advertisedLocalIp.size == 16) {
            "direct advertised address must be IPv4 or IPv6"
        }
        require(this.advertisedLocalIp.size == this.localBind.ipAddress.size) {
            "direct bind and advertised address families must match"
        }
        require(this.stunServers.size <= DIRECT_NATIVE_MAX_STUN_SERVERS) { "too many STUN servers" }
        require(this.stunServers.all { it.port >= 1_024 }) {
            "direct STUN endpoints must use a high non-zero port"
        }
        require(gatherTimeoutMillis in 100..5_000) {
            "direct gather timeout must be in 100..5000ms"
        }
    }
}

/** Fully owned input for V3 Nominate's exact embedded V2 start record. */
internal class DirectNativeNominateRequest(
    val epoch: Long,
    attemptId: ByteArray,
    connectionId: ByteArray,
    localDeviceId: ByteArray,
    peerDeviceId: ByteArray,
    val role: DirectNativeRole,
    val timeoutMillis: Int,
    val networkHandle: Long,
    localBind: DirectNativeSocketAddress,
    remoteCandidate: DirectNativeSocketAddress,
    ticket: ByteArray,
    serverSigningPublicKey: ByteArray,
    peerCertificateSha256: ByteArray,
    peerDeviceSigningPublicKey: ByteArray,
    localSendProbeHmacKey: ByteArray,
    peerReceiveProbeHmacKey: ByteArray,
    ticketId: ByteArray,
    nominationId: ByteArray,
    initiatorCandidateId: ByteArray,
    responderCandidateId: ByteArray,
    localProbeNonce: ByteArray,
    peerProbeNonce: ByteArray,
    localProofNonce: ByteArray,
    val nowMillis: Long,
) {
    val attemptId = attemptId.copyOf()
    val connectionId = connectionId.copyOf()
    val localDeviceId = localDeviceId.copyOf()
    val peerDeviceId = peerDeviceId.copyOf()
    val localBind = localBind.ownedCopy()
    val remoteCandidate = remoteCandidate.ownedCopy()
    val ticket = ticket.copyOf()
    val serverSigningPublicKey = serverSigningPublicKey.copyOf()
    val peerCertificateSha256 = peerCertificateSha256.copyOf()
    val peerDeviceSigningPublicKey = peerDeviceSigningPublicKey.copyOf()
    val localSendProbeHmacKey = localSendProbeHmacKey.copyOf()
    val peerReceiveProbeHmacKey = peerReceiveProbeHmacKey.copyOf()
    val ticketId = ticketId.copyOf()
    val nominationId = nominationId.copyOf()
    val initiatorCandidateId = initiatorCandidateId.copyOf()
    val responderCandidateId = responderCandidateId.copyOf()
    val localProbeNonce = localProbeNonce.copyOf()
    val peerProbeNonce = peerProbeNonce.copyOf()
    val localProofNonce = localProofNonce.copyOf()

    init {
        require(epoch in 1..Long.MAX_VALUE) { "direct path epoch must be positive" }
        require(timeoutMillis in 500..10_000) { "direct attempt timeout must be in 500..10000ms" }
        require(networkHandle != 0L) { "Android Network.getNetworkHandle() is required" }
        require(this.localBind.port >= 1_024 && this.remoteCandidate.port >= 1_024) {
            "direct nomination endpoints must use high non-zero ports"
        }
        listOf(
            this.attemptId, this.connectionId, this.localDeviceId, this.peerDeviceId,
            this.ticketId, this.nominationId, this.initiatorCandidateId,
            this.responderCandidateId, this.localProbeNonce, this.peerProbeNonce,
        ).forEach {
            require(it.isNonZeroIdentifier()) {
                "direct identifiers and probe nonces must be nonzero 16-byte values"
            }
        }
        require(!this.localDeviceId.contentEquals(this.peerDeviceId)) {
            "direct local and peer device ids must differ"
        }
        require(this.ticket.isNotEmpty() && this.ticket.size <= 2_048) {
            "direct ticket is empty or oversized"
        }
        listOf(
            this.serverSigningPublicKey, this.peerCertificateSha256,
            this.peerDeviceSigningPublicKey, this.localSendProbeHmacKey,
            this.peerReceiveProbeHmacKey,
        ).forEach { require(it.size == 32) { "direct keys and fingerprints must contain 32 bytes" } }
        require(this.localProofNonce.size in 16..32) {
            "direct local peer proof nonce must contain 16..32 bytes"
        }
        require(nowMillis > 0) { "direct nomination time must be positive" }
    }
}

internal enum class DirectNativeEventKind(val code: Int) {
    IDENTITY_READY(1), STATE_CHANGED(2), DIRECT_FAILED(3), SIGN_REQUEST(4), SHADOW_READY(5),
    IGNORED_STALE(6), CANDIDATES_READY(7), SYNTHETIC_FAILURE(1_000), UNKNOWN(-1);

    companion object {
        fun from(code: Int): DirectNativeEventKind = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

internal enum class DirectNativePathState(val code: Int) {
    RELAY_ONLY(1), GATHERING(2), PROBING(3), AUTHENTICATING(4), SHADOW_DIRECT(5), UNKNOWN(-1);

    companion object {
        fun from(code: Int): DirectNativePathState = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

internal object DirectNativeSyntheticReason {
    const val NATIVE_UNAVAILABLE = 1_001
    const val ABI_MISMATCH = 1_002
    const val FFI_CALL_FAILED = 1_003
    const val HANDLE_CLOSED = 1_004
    const val STALE_OWNER = 1_005
    const val CANDIDATES_NOT_EXCHANGED = 1_006
    const val SIGNATURE_NOT_REQUESTED = 1_007
    const val NOMINATION_ALREADY_SUBMITTED = 1_008
}

internal data class DirectNativeEvent(
    val kind: DirectNativeEventKind,
    val state: DirectNativePathState,
    val reason: Int,
    val epoch: Long,
    val generation: Long,
    val requestId: Long,
    val attemptId: ByteArray,
    val endpoint: DirectNativeSocketAddress?,
    val payload: ByteArray,
    val candidates: List<DirectNativeSocketAddress> = emptyList(),
) {
    companion object {
        fun relayOnly(reason: Int, epoch: Long = 0, attemptId: ByteArray = ByteArray(16)) =
            DirectNativeEvent(
                kind = DirectNativeEventKind.SYNTHETIC_FAILURE,
                state = DirectNativePathState.RELAY_ONLY,
                reason = reason,
                epoch = epoch,
                generation = 0,
                requestId = 0,
                attemptId = attemptId.copyOf(),
                endpoint = null,
                payload = ByteArray(0),
            )
    }
}

internal sealed interface DirectNativeResult<out T> {
    data class Success<T>(val value: T) : DirectNativeResult<T>
    data class Failure(val relayOnly: DirectNativeEvent, val nativeStatus: Int? = null) :
        DirectNativeResult<Nothing>
}

private fun ByteArray.isNonZeroIdentifier(): Boolean = size == 16 && any { it != 0.toByte() }

package dev.termlink.network.direct

import dev.termlink.protocol.transport.v1.DirectPathTicketV1

/**
 * Direct signalling is an optional, independently rollbackable control plane.
 * It deliberately defaults off and has no reference to the realtime Relay.
 */
internal data class DirectTransportSignalingFeature(
    val enabled: Boolean = false,
)

internal enum class DirectSignalingFaultCode {
    FEATURE_DISABLED,
    INVALID_CONFIGURATION,
    AUTHENTICATION_REJECTED,
    HTTP_UNAVAILABLE,
    RESPONSE_TOO_LARGE,
    RESPONSE_INVALID,
    TICKET_REJECTED,
    SIGNAL_CONNECT_FAILED,
    SIGNAL_PROTOCOL_REJECTED,
    SIGNAL_FRAME_REJECTED,
    SIGNAL_REPLAYED,
    SIGNAL_QUEUE_OVERFLOW,
    RETRY_EXHAUSTED,
    CLOSED,
}

internal data class DirectSignalingFault(
    val code: DirectSignalingFaultCode,
    val httpStatus: Int? = null,
)

internal sealed interface DirectSignalingCallResult<out T> {
    data class Success<T>(val value: T) : DirectSignalingCallResult<T>
    data object Disabled : DirectSignalingCallResult<Nothing>
    data class Fault(val fault: DirectSignalingFault) : DirectSignalingCallResult<Nothing>
}

internal data class DirectTicketSigningKey(
    val keyId: String,
    val publicKey: ByteArray,
) {
    init {
        require(keyId.matches(KEY_ID_PATTERN)) { "ticket signing key ID is invalid" }
        require(publicKey.size == ED25519_PUBLIC_KEY_BYTES) { "ticket signing key must be Ed25519" }
    }

    override fun equals(other: Any?): Boolean =
        other is DirectTicketSigningKey && keyId == other.keyId && publicKey.contentEquals(other.publicKey)

    override fun hashCode(): Int = 31 * keyId.hashCode() + publicKey.contentHashCode()

    internal companion object {
        internal const val ED25519_PUBLIC_KEY_BYTES = 32
        internal val KEY_ID_PATTERN = Regex("[A-Za-z0-9._:-]{1,64}")
    }
}

internal data class DirectTransportServerConfig(
    val protocolVersion: Int,
    val capabilities: Set<String>,
    /** Server routing gate; true is valid only with `direct_transport_data_v1`. */
    val directRoutingPermitted: Boolean,
    val ticketSigningKeys: Map<String, DirectTicketSigningKey>,
    val stunServers: List<String>,
)

internal data class ValidatedDirectPathTicket(
    val protobuf: DirectPathTicketV1,
    val wireBytes: ByteArray,
) {
    override fun equals(other: Any?): Boolean =
        other is ValidatedDirectPathTicket && protobuf == other.protobuf && wireBytes.contentEquals(other.wireBytes)

    override fun hashCode(): Int = 31 * protobuf.hashCode() + wireBytes.contentHashCode()
}

internal sealed interface DirectTransportSignalingState {
    data object Disabled : DirectTransportSignalingState
    data object Idle : DirectTransportSignalingState
    data class Connecting(val attempt: Int) : DirectTransportSignalingState
    data class Connected(val generation: Int) : DirectTransportSignalingState
    data class Fault(
        val fault: DirectSignalingFault,
        val willRetry: Boolean,
        val attempt: Int,
    ) : DirectTransportSignalingState
    data object Closed : DirectTransportSignalingState
}

internal fun interface DirectSignalingTicketSignatureVerifier {
    /** Verifies Ed25519 over the exact domain-separated protobuf claim bytes. */
    fun verify(publicKey: ByteArray, signedBytes: ByteArray, signature: ByteArray): Boolean
}

internal fun interface DirectSignalingRetryDelay {
    suspend fun await(milliseconds: Long)
}

internal fun interface DirectSignalingRetryJitter {
    fun apply(baseMilliseconds: Long): Long
}

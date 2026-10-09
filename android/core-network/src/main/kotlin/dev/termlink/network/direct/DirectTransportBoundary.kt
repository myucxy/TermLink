package dev.termlink.network.direct

import java.net.InetAddress

/**
 * Experimental direct-path foundation. This flag deliberately defaults to false and this package
 * is not wired into the production realtime client yet.
 *
 * Termux lifecycle reference boundary: `termux/termux-app@3df69d1d` TermuxService warns that Android
 * may not finish `onDestroy()`, issues #377/#2366 document Doze/background limits, and PR #5094
 * demonstrates stale activity teardown racing a newer owner. TermLink therefore fences optional
 * network work by the exact epoch/attempt/network owner and treats lifecycle cleanup as best effort.
 * The historical reference excludes remote transport/authentication; no Termux PTY,
 * process, local socket, or GPL network implementation is reused here.
 */
internal data class DirectTransportFeature(
    val enabled: Boolean = false,
)

internal enum class DirectCandidateKind {
    HOST,
    SERVER_REFLEXIVE,
}

/** A process-memory-only endpoint candidate. IP bytes are never rendered or persisted. */
internal data class DirectCandidate(
    val id: ByteArray,
    val kind: DirectCandidateKind,
    val address: InetAddress,
    val port: Int,
    val priority: Int,
    val prefixLength: Int? = null,
    val relatedCandidateId: ByteArray? = null,
) {
    init {
        require(id.size == ID_BYTES) { "candidate id must be 16 bytes" }
        require(address.address.size == IPV4_BYTES || address.address.size == IPV6_BYTES) {
            "candidate address must be IPv4 or IPv6"
        }
        require(port in MIN_CANDIDATE_PORT..MAX_CANDIDATE_PORT) { "candidate port is out of range" }
        require(priority > 0) { "candidate priority must be positive" }
        require(prefixLength == null || prefixLength in 0..(address.address.size * Byte.SIZE_BITS)) {
            "candidate prefix length is out of range"
        }
        when (kind) {
            DirectCandidateKind.HOST -> require(relatedCandidateId == null) {
                "host candidate must not have a related candidate"
            }

            DirectCandidateKind.SERVER_REFLEXIVE -> require(relatedCandidateId?.size == ID_BYTES) {
                "server-reflexive candidate must reference one host candidate"
            }
        }
    }

    override fun equals(other: Any?): Boolean =
        other is DirectCandidate && id.contentEquals(other.id) && kind == other.kind &&
            address == other.address && port == other.port && priority == other.priority &&
            prefixLength == other.prefixLength &&
            when {
                relatedCandidateId == null -> other.relatedCandidateId == null
                other.relatedCandidateId == null -> false
                else -> relatedCandidateId.contentEquals(other.relatedCandidateId)
            }

    override fun hashCode(): Int {
        var result = id.contentHashCode()
        result = 31 * result + kind.hashCode()
        result = 31 * result + address.hashCode()
        result = 31 * result + port
        result = 31 * result + priority
        result = 31 * result + (prefixLength ?: 0)
        result = 31 * result + (relatedCandidateId?.contentHashCode() ?: 0)
        return result
    }

    internal companion object {
        internal const val ID_BYTES = 16
        internal const val IPV4_BYTES = 4
        internal const val IPV6_BYTES = 16
        internal const val MIN_CANDIDATE_PORT = 1024
        internal const val MAX_CANDIDATE_PORT = 65_535
    }
}

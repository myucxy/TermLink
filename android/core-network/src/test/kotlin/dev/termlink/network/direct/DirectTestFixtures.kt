package dev.termlink.network.direct

import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.ArrayDeque

internal class FakeDirectDatagramEndpoint(
    override val localPort: Int = 45_000,
) : DirectDatagramEndpoint {
    val sent = mutableListOf<Pair<ByteArray, InetSocketAddress>>()
    val incoming = ArrayDeque<DirectReceivedDatagram>()
    var closeCount = 0
    var receiveCount = 0
    var onSend: ((ByteArray, InetSocketAddress) -> Unit)? = null

    override val isClosed: Boolean
        get() = closeCount > 0

    override fun send(payload: ByteArray, target: InetSocketAddress) {
        check(!isClosed)
        sent += payload.copyOf() to target
        onSend?.invoke(payload, target)
    }

    override fun receive(maxBytes: Int, timeoutMillis: Int): DirectReceivedDatagram {
        receiveCount++
        val datagram = if (incoming.isEmpty()) {
            throw SocketTimeoutException("test timeout")
        } else {
            incoming.removeFirst()
        }
        require(datagram.payload.size <= maxBytes)
        return datagram
    }

    override fun close() {
        closeCount++
    }
}

internal fun testCandidate(
    idByte: Int,
    kind: DirectCandidateKind,
    address: String,
    port: Int = 45_000 + idByte,
    priority: Int = 1_000 - idByte,
    prefixLength: Int? = if (address.contains(':')) 64 else 24,
    relatedIdByte: Int = 99,
): DirectCandidate = DirectCandidate(
    id = ByteArray(16) { idByte.toByte() },
    kind = kind,
    address = InetAddress.getByName(address),
    port = port,
    priority = priority,
    prefixLength = prefixLength,
    relatedCandidateId = if (kind == DirectCandidateKind.SERVER_REFLEXIVE) {
        ByteArray(16) { relatedIdByte.toByte() }
    } else null,
)

internal fun testProbeTemplate(
    senderByte: Int,
    localCandidate: DirectCandidate,
    remoteCandidate: DirectCandidate,
): DirectProbeTemplate = DirectProbeTemplate(
    connectionId = ByteArray(16) { 1 },
    pathEpoch = 7,
    ticketId = ByteArray(16) { 2 },
    nominationId = ByteArray(16) { 3 },
    initiatorCandidateId = localCandidate.id,
    responderCandidateId = remoteCandidate.id,
    senderDeviceId = ByteArray(16) { senderByte.toByte() },
)

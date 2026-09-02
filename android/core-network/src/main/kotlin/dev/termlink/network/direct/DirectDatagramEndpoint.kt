package dev.termlink.network.direct

import java.io.Closeable
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicBoolean

internal data class DirectReceivedDatagram(
    val payload: ByteArray,
    val source: InetSocketAddress,
)

/**
 * The one UDP endpoint owned by a direct-path epoch. Host candidates, STUN and connectivity probes
 * all use [localPort]; later QUIC integration must also adopt this exact socket rather than rebinding.
 */
internal interface DirectDatagramEndpoint : Closeable {
    val localPort: Int
    val isClosed: Boolean

    fun send(payload: ByteArray, target: InetSocketAddress)

    fun receive(maxBytes: Int, timeoutMillis: Int): DirectReceivedDatagram
}

internal fun interface DirectDatagramEndpointFactory {
    fun open(): DirectDatagramEndpoint
}

internal class SingleSocketDirectDatagramEndpoint private constructor(
    private val socket: DatagramSocket,
) : DirectDatagramEndpoint {
    private val receiving = AtomicBoolean(false)

    override val localPort: Int
        get() = socket.localPort

    override val isClosed: Boolean
        get() = socket.isClosed

    override fun send(payload: ByteArray, target: InetSocketAddress) {
        require(payload.isNotEmpty() && payload.size <= MAX_DATAGRAM_BYTES) {
            "direct datagram size is out of range"
        }
        require(!target.isUnresolved) { "direct datagram target must be an IP endpoint" }
        require(target.port in 1..65_535) { "direct datagram target port is out of range" }
        check(!socket.isClosed) { "direct datagram endpoint is closed" }
        socket.send(DatagramPacket(payload, payload.size, target))
    }

    override fun receive(maxBytes: Int, timeoutMillis: Int): DirectReceivedDatagram {
        require(maxBytes in 1..MAX_DATAGRAM_BYTES) { "receive limit is out of range" }
        require(timeoutMillis in 1..MAX_RECEIVE_TIMEOUT_MILLIS) { "receive timeout is out of range" }
        check(receiving.compareAndSet(false, true)) { "only one receive owner is allowed per UDP epoch" }
        try {
            check(!socket.isClosed) { "direct datagram endpoint is closed" }
            socket.soTimeout = timeoutMillis
            val buffer = ByteArray(maxBytes + 1)
            val packet = DatagramPacket(buffer, buffer.size)
            socket.receive(packet)
            require(packet.length <= maxBytes) { "direct datagram exceeded the receive limit" }
            val source = InetSocketAddress(packet.address, packet.port)
            return DirectReceivedDatagram(buffer.copyOf(packet.length), source)
        } finally {
            receiving.set(false)
        }
    }

    override fun close() = socket.close()

    internal companion object {
        internal const val MAX_DATAGRAM_BYTES = 2_048
        internal const val MAX_RECEIVE_TIMEOUT_MILLIS = 2_000

        internal fun open(): SingleSocketDirectDatagramEndpoint {
            val socket = DatagramSocket(null)
            try {
                socket.reuseAddress = false
                socket.bind(InetSocketAddress(0))
                check(socket.localPort in DirectCandidate.MIN_CANDIDATE_PORT..DirectCandidate.MAX_CANDIDATE_PORT) {
                    "operating system selected an invalid direct-path port"
                }
                return SingleSocketDirectDatagramEndpoint(socket)
            } catch (error: Throwable) {
                socket.close()
                throw error
            }
        }

        internal val factory = DirectDatagramEndpointFactory(::open)
    }
}

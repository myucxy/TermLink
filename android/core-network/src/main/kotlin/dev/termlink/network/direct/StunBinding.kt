package dev.termlink.network.direct

import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import kotlin.math.min

internal class StunValidationException(message: String) : Exception(message)

internal data class StunBindingResult(
    val mappedEndpoint: InetSocketAddress,
    /** Proves discovery used the same epoch socket represented by every host candidate. */
    val localPort: Int,
)

internal object StunBindingCodec {
    internal const val HEADER_BYTES = 20
    internal const val TRANSACTION_ID_BYTES = 12
    internal const val MAGIC_COOKIE = 0x2112A442
    internal const val MAX_MESSAGE_BYTES = 1_024
    private const val BINDING_REQUEST = 0x0001
    private const val BINDING_SUCCESS_RESPONSE = 0x0101
    private const val XOR_MAPPED_ADDRESS = 0x0020

    fun bindingRequest(transactionId: ByteArray): ByteArray {
        require(transactionId.size == TRANSACTION_ID_BYTES) { "STUN transaction id must be 12 bytes" }
        return ByteBuffer.allocate(HEADER_BYTES).order(ByteOrder.BIG_ENDIAN)
            .putShort(BINDING_REQUEST.toShort())
            .putShort(0.toShort())
            .putInt(MAGIC_COOKIE)
            .put(transactionId)
            .array()
    }

    @Throws(StunValidationException::class)
    fun parseBindingSuccess(
        packet: ByteArray,
        actualSource: InetSocketAddress,
        expectedSource: InetSocketAddress,
        expectedTransactionId: ByteArray,
    ): InetSocketAddress {
        rejectUnless(packet.size in HEADER_BYTES..MAX_MESSAGE_BYTES, "STUN response size is invalid")
        rejectUnless(!expectedSource.isUnresolved, "configured STUN source is unresolved")
        rejectUnless(sameEndpoint(actualSource, expectedSource), "STUN response source mismatch")
        rejectUnless(expectedTransactionId.size == TRANSACTION_ID_BYTES, "STUN transaction id is invalid")

        val reader = ByteBuffer.wrap(packet).order(ByteOrder.BIG_ENDIAN)
        rejectUnless(reader.short.toInt() and 0xffff == BINDING_SUCCESS_RESPONSE, "not a STUN Binding success")
        val messageLength = reader.short.toInt() and 0xffff
        rejectUnless(messageLength % 4 == 0, "STUN message length is not aligned")
        rejectUnless(messageLength == packet.size - HEADER_BYTES, "STUN message length mismatch")
        val cookie = reader.int
        rejectUnless(cookie == MAGIC_COOKIE, "STUN magic cookie mismatch")
        val transactionId = ByteArray(TRANSACTION_ID_BYTES).also(reader::get)
        rejectUnless(
            MessageDigest.isEqual(transactionId, expectedTransactionId),
            "STUN transaction id mismatch",
        )

        var mapped: InetSocketAddress? = null
        while (reader.hasRemaining()) {
            rejectUnless(reader.remaining() >= 4, "truncated STUN attribute header")
            val type = reader.short.toInt() and 0xffff
            val length = reader.short.toInt() and 0xffff
            rejectUnless(length <= reader.remaining(), "truncated STUN attribute value")
            val value = ByteArray(length).also(reader::get)
            val padding = (4 - (length % 4)) % 4
            rejectUnless(padding <= reader.remaining(), "truncated STUN attribute padding")
            reader.position(reader.position() + padding)
            if (type == XOR_MAPPED_ADDRESS) {
                rejectUnless(mapped == null, "duplicate XOR-MAPPED-ADDRESS")
                mapped = decodeXorMapped(value, cookie, transactionId)
            }
        }
        return mapped ?: throw StunValidationException("missing XOR-MAPPED-ADDRESS")
    }

    private fun decodeXorMapped(value: ByteArray, cookie: Int, transactionId: ByteArray): InetSocketAddress {
        rejectUnless(value.size >= 4 && value[0] == 0.toByte(), "invalid XOR-MAPPED-ADDRESS header")
        val family = value[1].toInt() and 0xff
        val expectedLength = when (family) {
            0x01 -> 8
            0x02 -> 20
            else -> throw StunValidationException("unsupported XOR-MAPPED-ADDRESS family")
        }
        rejectUnless(value.size == expectedLength, "invalid XOR-MAPPED-ADDRESS length")
        val encodedPort = ((value[2].toInt() and 0xff) shl 8) or (value[3].toInt() and 0xff)
        val port = encodedPort xor (cookie ushr 16)
        val cookieBytes = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(cookie).array()
        val mask = if (family == 0x01) cookieBytes else cookieBytes + transactionId
        val addressBytes = ByteArray(expectedLength - 4) { index ->
            (value[index + 4].toInt() xor mask[index].toInt()).toByte()
        }
        val address = try {
            InetAddress.getByAddress(addressBytes)
        } catch (error: Exception) {
            throw StunValidationException("invalid XOR-MAPPED-ADDRESS bytes")
        }
        rejectUnless(
            DirectAddressPolicy.isCandidateEndpoint(address, port),
            "XOR-MAPPED-ADDRESS is not a safe candidate endpoint",
        )
        return InetSocketAddress(address, port)
    }

    private fun sameEndpoint(left: InetSocketAddress, right: InetSocketAddress): Boolean =
        left.port == right.port && !left.isUnresolved && !right.isUnresolved &&
            left.address.address.contentEquals(right.address.address)

    private fun rejectUnless(condition: Boolean, message: String) {
        if (!condition) throw StunValidationException(message)
    }
}

internal fun interface StunTransactionIdSource {
    fun next(): ByteArray
}

internal class StunBindingClient(
    private val transactionIdSource: StunTransactionIdSource = StunTransactionIdSource {
        ByteArray(StunBindingCodec.TRANSACTION_ID_BYTES).also(SecureRandom()::nextBytes)
    },
) {
    fun discover(
        endpoint: DirectDatagramEndpoint,
        server: InetSocketAddress,
        totalTimeoutMillis: Int = DEFAULT_TOTAL_TIMEOUT_MILLIS,
    ): StunBindingResult {
        require(!server.isUnresolved) { "STUN server must be pre-resolved from authenticated config" }
        require(server.port in 1..65_535) { "STUN server port is out of range" }
        require(totalTimeoutMillis in 1..MAX_TOTAL_TIMEOUT_MILLIS) { "STUN timeout is out of range" }
        val transactionId = transactionIdSource.next().copyOf()
        require(transactionId.size == StunBindingCodec.TRANSACTION_ID_BYTES) {
            "STUN transaction id source returned an invalid id"
        }
        val request = StunBindingCodec.bindingRequest(transactionId)
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(totalTimeoutMillis.toLong())
        var packetsSeen = 0
        var attempt = 0
        var lastValidationFailure: StunValidationException? = null
        while (attempt++ < MAX_SEND_ATTEMPTS) {
            endpoint.send(request, server)
            val attemptDeadline = min(
                deadline,
                System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(RECEIVE_SLICE_MILLIS.toLong()),
            )
            while (System.nanoTime() < attemptDeadline && packetsSeen++ < MAX_RECEIVED_PACKETS) {
                val remainingMillis = TimeUnit.NANOSECONDS.toMillis(attemptDeadline - System.nanoTime())
                    .coerceIn(1L, RECEIVE_SLICE_MILLIS.toLong())
                    .toInt()
                val datagram = try {
                    endpoint.receive(StunBindingCodec.MAX_MESSAGE_BYTES, remainingMillis)
                } catch (_: SocketTimeoutException) {
                    break
                }
                try {
                    val mapped = StunBindingCodec.parseBindingSuccess(
                        datagram.payload,
                        datagram.source,
                        server,
                        transactionId,
                    )
                    return StunBindingResult(mapped, endpoint.localPort)
                } catch (invalid: StunValidationException) {
                    lastValidationFailure = invalid
                }
            }
            if (System.nanoTime() >= deadline || packetsSeen >= MAX_RECEIVED_PACKETS) break
        }
        throw SocketTimeoutException(
            lastValidationFailure?.let { "valid STUN response not received: ${it.message}" }
                ?: "STUN Binding request timed out",
        )
    }

    internal companion object {
        internal const val DEFAULT_TOTAL_TIMEOUT_MILLIS = 1_500
        internal const val MAX_TOTAL_TIMEOUT_MILLIS = 10_000
        internal const val RECEIVE_SLICE_MILLIS = 400
        internal const val MAX_SEND_ATTEMPTS = 4
        internal const val MAX_RECEIVED_PACKETS = 32
    }
}

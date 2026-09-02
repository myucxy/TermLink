package dev.termlink.network.direct

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class StunBindingTest {
    private val transactionId = ByteArray(StunBindingCodec.TRANSACTION_ID_BYTES) { (it + 1).toByte() }
    private val source = InetSocketAddress(InetAddress.getByName("127.0.0.1"), 34_789)

    @Test
    fun bindingRequestUsesRfc8489Header() {
        val request = StunBindingCodec.bindingRequest(transactionId)

        assertEquals(StunBindingCodec.HEADER_BYTES, request.size)
        assertEquals(0x0001, ushort(request, 0))
        assertEquals(0, ushort(request, 2))
        assertEquals(StunBindingCodec.MAGIC_COOKIE, ByteBuffer.wrap(request, 4, 4).int)
        assertArrayEquals(transactionId, request.copyOfRange(8, 20))
    }

    @Test
    fun parserAcceptsExactSourceTransactionAndXorMappedAddress() {
        val response = response(transactionId, "1.1.1.1", 50_321)

        val mapped = StunBindingCodec.parseBindingSuccess(response, source, source, transactionId)

        assertEquals("1.1.1.1", mapped.address.hostAddress)
        assertEquals(50_321, mapped.port)
    }

    @Test
    fun parserDecodesIpv6XorMappedAddressWithCookieAndTransactionMask() {
        val response = response(transactionId, "2001:4860:4860::8888", 50_322)

        val mapped = StunBindingCodec.parseBindingSuccess(response, source, source, transactionId)

        assertArrayEquals(InetAddress.getByName("2001:4860:4860::8888").address, mapped.address.address)
        assertEquals(50_322, mapped.port)
    }

    @Test
    fun parserRejectsSpoofedOrMalformedResponses() {
        val valid = response(transactionId, "1.1.1.1", 50_321)
        val badCookie = valid.copyOf().also { it[4] = (it[4].toInt() xor 1).toByte() }
        val badTransaction = valid.copyOf().also { it[8] = (it[8].toInt() xor 1).toByte() }
        val wrongSource = InetSocketAddress(source.address, source.port + 1)
        val duplicateMapped = duplicateXorMapped(valid)
        val unsafeMapped = response(transactionId, "127.0.0.1", 50_321)
        val truncated = valid.copyOf(valid.size - 1)

        listOf<Pair<String, () -> Unit>>(
            "cookie" to { StunBindingCodec.parseBindingSuccess(badCookie, source, source, transactionId) },
            "transaction" to { StunBindingCodec.parseBindingSuccess(badTransaction, source, source, transactionId) },
            "source" to { StunBindingCodec.parseBindingSuccess(valid, wrongSource, source, transactionId) },
            "duplicate" to { StunBindingCodec.parseBindingSuccess(duplicateMapped, source, source, transactionId) },
            "unsafe address" to { StunBindingCodec.parseBindingSuccess(unsafeMapped, source, source, transactionId) },
            "truncated" to { StunBindingCodec.parseBindingSuccess(truncated, source, source, transactionId) },
        ).forEach { (label, action) ->
            assertThrows(label, StunValidationException::class.java, action)
        }
    }

    @Test
    fun discoveryUsesTheSameUdpSocketAndPortAsCandidates() {
        val server = DatagramSocket(InetSocketAddress(InetAddress.getByName("127.0.0.1"), 0))
        val endpoint = SingleSocketDirectDatagramEndpoint.open()
        var observedClientPort = -1
        val responder = thread(name = "stun-test-responder") {
            val requestBytes = ByteArray(128)
            val request = DatagramPacket(requestBytes, requestBytes.size)
            server.receive(request)
            observedClientPort = request.port
            val requestTransaction = request.data.copyOfRange(request.offset + 8, request.offset + 20)
            val response = response(requestTransaction, "1.1.1.1", 51_234)
            server.send(DatagramPacket(response, response.size, request.address, request.port))
        }
        try {
            val hostCandidate = DirectCandidate(
                id = ByteArray(16) { 7 },
                kind = DirectCandidateKind.HOST,
                address = InetAddress.getByName("192.168.1.2"),
                port = endpoint.localPort,
                priority = 100,
                prefixLength = 24,
            )
            val result = StunBindingClient(StunTransactionIdSource { transactionId }).discover(
                endpoint,
                InetSocketAddress(server.localAddress, server.localPort),
            )

            responder.join(2_000)
            assertTrue(!responder.isAlive)
            assertEquals(hostCandidate.port, result.localPort)
            assertEquals(endpoint.localPort, observedClientPort)
            assertEquals(51_234, result.mappedEndpoint.port)
        } finally {
            endpoint.close()
            server.close()
            responder.join(2_000)
        }
    }

    @Test
    fun discoveryTimeoutIsBoundedToFourSends() {
        val endpoint = FakeDirectDatagramEndpoint()
        val client = StunBindingClient(StunTransactionIdSource { transactionId })

        assertThrows(SocketTimeoutException::class.java) {
            client.discover(endpoint, source, totalTimeoutMillis = 50)
        }

        assertTrue(endpoint.sent.size in 1..StunBindingClient.MAX_SEND_ATTEMPTS)
        assertTrue(endpoint.receiveCount <= StunBindingClient.MAX_SEND_ATTEMPTS)
    }

    private fun response(transaction: ByteArray, mappedAddress: String, mappedPort: Int): ByteArray {
        val address = InetAddress.getByName(mappedAddress).address
        val cookieBytes = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN)
            .putInt(StunBindingCodec.MAGIC_COOKIE)
            .array()
        val mask = if (address.size == 4) cookieBytes else cookieBytes + transaction
        val value = ByteArray(4 + address.size)
        value[1] = if (address.size == 4) 0x01 else 0x02
        val encodedPort = mappedPort xor (StunBindingCodec.MAGIC_COOKIE ushr 16)
        value[2] = (encodedPort ushr 8).toByte()
        value[3] = encodedPort.toByte()
        repeat(address.size) { index ->
            value[index + 4] = (address[index].toInt() xor mask[index].toInt()).toByte()
        }
        val attributeBytes = 4 + value.size
        return ByteBuffer.allocate(StunBindingCodec.HEADER_BYTES + attributeBytes).order(ByteOrder.BIG_ENDIAN)
            .putShort(0x0101.toShort())
            .putShort(attributeBytes.toShort())
            .putInt(StunBindingCodec.MAGIC_COOKIE)
            .put(transaction)
            .putShort(0x0020.toShort())
            .putShort(value.size.toShort())
            .put(value)
            .array()
    }

    private fun duplicateXorMapped(valid: ByteArray): ByteArray {
        val duplicate = valid.copyOfRange(20, 32)
        return (valid + duplicate).also { bytes ->
            bytes[2] = 0
            bytes[3] = 24
        }
    }

    private fun ushort(bytes: ByteArray, offset: Int): Int =
        ((bytes[offset].toInt() and 0xff) shl 8) or (bytes[offset + 1].toInt() and 0xff)
}

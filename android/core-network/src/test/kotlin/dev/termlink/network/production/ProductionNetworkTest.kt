package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString as OkioByteString
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductionNetworkTest {
    @Test
    fun apiRequestCarriesValidOidcAndDeviceProof() {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(200).setHeader("Content-Type", "application/json").setBody("{\"items\":[]}"))
        server.start()
        val identity = DeviceIdentity.generate()
        val deviceId = UUID.randomUUID()
        try {
            ProductionApiClient(server.url("/").toString(), AccessTokenProvider { "oidc-token" }, deviceId, identity, allowCleartextForLocalTest = true).use {
                it.get("/v1/devices")
            }
            val request = server.takeRequest()
            assertEquals("Bearer oidc-token", request.getHeader("Authorization"))
            assertEquals(deviceId.toString(), request.getHeader("X-TermLink-Device"))
            val timestamp = request.getHeader("X-TermLink-Timestamp")!!
            val nonce = request.getHeader("X-TermLink-Nonce")!!
            val signature = java.util.Base64.getUrlDecoder().decode(request.getHeader("X-TermLink-Signature"))
            val bodyHash = MessageDigest.getInstance("SHA-256").digest(ByteArray(0)).joinToString("") { "%02x".format(it) }
            val canonical = listOf("TERMLINK-DEVICE-PROOF-V1", "GET", "/v1/devices", timestamp, nonce, bodyHash).joinToString("\n").encodeToByteArray()
            val verifier = Ed25519Signer().apply { init(false, Ed25519PublicKeyParameters(identity.signingPublicKey, 0)); update(canonical, 0, canonical.size) }
            assertTrue(verifier.verifySignature(signature))
        } finally {
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun productionEnvelopeMatchesCrossPlatformCiphertextVector() {
        val envelope = ProductionRealtimeClient.encryptedEnvelope(
            sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee"),
            senderId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"),
            messageType = MessageType.OUTPUT_CHUNK,
            direction = CryptoDirection.PC_TO_ANDROID,
            sequence = 42,
            acknowledgement = 7,
            keyId = "epoch-00000007".encodeToByteArray(),
            nonce = hex("aabbccdd000000000000002a"),
            sessionKey = hex("e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c"),
            plaintext = hex("1b5b33316d5465726d4c696e6b1b5b306d0d0a"),
            nowMillis = 1_800_000_000_123,
        )
        assertEquals("6a97c75f0d8c984923467129d973a325134d69c9bd792587797651196a7498a68e3c44", envelope.ciphertext.toByteArray().toHex())
        assertEquals("1b5b33316d5465726d4c696e6b1b5b306d0d0a", ProductionRealtimeClient.decryptEnvelope(envelope, hex("e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c")).toHex())
    }

    @Test
    fun androidTransportAcceptsOutputReplayOnTheCurrentSocket(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        listOf(1L, 2L, 1L, 3L).forEach { sequence ->
                            check(webSocket.send(OkioByteString.of(*testEnvelope(sessionId, pcId, androidId, sequence).toByteArray())))
                        }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        try {
            val received = mutableListOf<Envelope>()
            val collector = launch(start = CoroutineStart.UNDISPATCHED) {
                client.incoming.take(4).toList(received)
            }
            client.connect()
            withTimeout(5_000) { collector.join() }
            assertEquals(listOf(1L, 2L, 1L, 3L), received.map(Envelope::getSeq))
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun receiveOverflowKeepsOneContiguousPrefixForAckResume(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        for (sequence in 1L..300L) {
                            check(webSocket.send(OkioByteString.of(*testEnvelope(sessionId, pcId, androidId, sequence).toByteArray())))
                        }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        val firstReceived = CompletableDeferred<Unit>()
        val releaseCollector = CompletableDeferred<Unit>()
        val received = CopyOnWriteArrayList<Long>()
        val collector = launch(start = CoroutineStart.UNDISPATCHED) {
            client.incoming.collect { envelope ->
                received += envelope.seq
                if (envelope.seq == 1L) {
                    firstReceived.complete(Unit)
                    releaseCollector.await()
                }
            }
        }
        val failed = async(start = CoroutineStart.UNDISPATCHED) {
            client.state.first { it is RealtimeState.Failed }
        }
        try {
            client.connect()
            withTimeout(5_000) { firstReceived.await() }
            withTimeout(5_000) { failed.await() }
            releaseCollector.complete(Unit)
            withTimeout(5_000) {
                while (received.size < 2) delay(10)
                delay(100)
            }
            assertTrue("overflow must stop before all frames", received.last() < 300L)
            assertTrue(
                "overflow must retain one contiguous prefix: $received",
                received.zipWithNext().all { (left, right) -> right == left + 1L },
            )
        } finally {
            releaseCollector.complete(Unit)
            collector.cancelAndJoin()
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    private fun testEnvelope(sessionId: UUID, pcId: UUID, androidId: UUID, sequence: Long): Envelope =
        Envelope.newBuilder()
            .setProtocolVersion(1)
            .setMessageType(MessageType.OUTPUT_CHUNK)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setSenderDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pcId)))
            .setRecipientDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(androidId)))
            .setDirection(Direction.PC_TO_ANDROID)
            .setSeq(sequence)
            .setSentAtMs(1_800_000_000_000L + sequence)
            .setKeyId(ByteString.copyFromUtf8("epoch-00000001"))
            .setNonce(ByteString.copyFrom(ByteArray(12) { sequence.toByte() }))
            .setCiphertext(ByteString.copyFrom(ByteArray(16) { sequence.toByte() }))
            .build()

    private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}

package dev.termlink.network.production

import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import java.security.MessageDigest
import java.util.UUID
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import dev.termlink.protocol.realtime.v1.MessageType

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

    private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}

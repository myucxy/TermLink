package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import java.util.Base64
import java.util.UUID
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Test

class ProductContractsTest {
    @Test
    fun serverUrlPolicyRequiresHttpsUnlessExplicitlyTestingCleartext() {
        assertEquals("https://example.com:8443", ServerUrlPolicy.normalize(" HTTPS://Example.com:8443/ "))
        assertEquals("http://127.0.0.1:8080", ServerUrlPolicy.normalize("http://127.0.0.1:8080", allowCleartextForLocalTest = true))
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("http://127.0.0.1:8080") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://user:pass@example.com") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://example.com/api") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://example.com?debug=true") }
    }

    @Test
    fun pairingParserCanonicalizesCodeAndNonce() {
        val id = UUID.randomUUID()
        val nonceBytes = ByteArray(32) { it.toByte() }
        val nonce = Base64.getUrlEncoder().withoutPadding().encodeToString(nonceBytes)
        val parsed = PairingInputParser.parse(id.toString(), " abcd-efgh ", nonce)

        assertEquals(id, parsed.challengeId)
        assertEquals("ABCDEFGH", parsed.code)
        assertEquals(nonce, parsed.challengeNonce)
        assertThrows(IllegalArgumentException::class.java) {
            PairingInputParser.parse(id.toString(), "AAAA-AAAA", "bad")
        }
    }

    @Test
    fun pendingAndTrustedDeviceResponsesRemainServerAuthoritative() {
        val id = UUID.randomUUID()
        val pending = parseDevice(id, "PENDING")
        val trusted = parseDevice(id, "TRUSTED")

        assertEquals(DeviceTrustState.PENDING, pending.trustState)
        assertEquals(DeviceTrustState.TRUSTED, trusted.trustState)
        assertEquals(id, pending.id)
        assertEquals(id, trusted.id)
    }

    @Test
    fun authStateEnvelopeUsesCipherAndDoesNotStorePlaintext() {
        val cipher = FakeCipher()
        val plaintext = "refresh-token-secret".encodeToByteArray()

        val sealed = AuthStateEnvelope.seal(plaintext, cipher)

        assertFalse(sealed.contains("refresh-token-secret"))
        assertEquals(plaintext.toList(), AuthStateEnvelope.open(sealed, cipher).toList())
        assertEquals(1, cipher.encryptions)
        assertEquals(1, cipher.decryptions)
    }

    @Test
    fun tokenRefreshCoordinatorCallsRefreshAndRejectsBlankTokens() {
        runBlocking {
            var calls = 0
            val coordinator = TokenRefreshCoordinator {
                calls += 1
                "fresh-$calls"
            }

            assertEquals("fresh-1", coordinator.freshAccessToken())
            assertEquals("fresh-2", coordinator.freshAccessToken())
            assertEquals(2, calls)
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { TokenRefreshCoordinator { " " }.freshAccessToken() }
            }
        }
    }

    private fun parseDevice(id: UUID, trustState: String) = ProductResponseParser.device(
        Json.parseToJsonElement(
            """
            {
              "id":"$id",
              "type":"ANDROID",
              "name":"Test phone",
              "key_id":"device-key",
              "exchange_public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "trust_state":"$trustState",
              "created_at":"2026-08-04T00:00:00Z"
            }
            """.trimIndent(),
        ).jsonObject,
    )

    private class FakeCipher : AuthStateCipher {
        var encryptions = 0
        var decryptions = 0

        override fun transform(input: ByteArray, encrypt: Boolean): ByteArray {
            if (encrypt) encryptions++ else decryptions++
            return if (encrypt) {
                ByteArray(12) { 0x5A } + input.map { (it.toInt() xor 0xA5).toByte() }.toByteArray()
            } else {
                input.copyOfRange(12, input.size).map { (it.toInt() xor 0xA5).toByte() }.toByteArray()
            }
        }
    }
}

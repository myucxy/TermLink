package dev.termlink.crypto

import dev.termlink.protocol.realtime.v1.OutputChunkPayload
import dev.termlink.protocol.realtime.v1.ResizePayload
import java.security.SecureRandom
import java.util.UUID
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class SessionCryptoTest {
    @Test
    fun x25519AndDirectionalAeadRoundTrip() {
        val pc = DeviceExchangeKeyPair.generate(SecureRandom())
        val android = DeviceExchangeKeyPair.generate(SecureRandom())
        val pcId = UUID.randomUUID()
        val androidId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        val keyId = "epoch-00000001".encodeToByteArray()
        val pcPair = SessionCrypto.derivePairwiseKey(pc.privateKey, android.publicKey, pcId, androidId, 1)
        val androidPair = SessionCrypto.derivePairwiseKey(android.privateKey, pc.publicKey, pcId, androidId, 1)
        assertArrayEquals(pcPair, androidPair)
        val outputKey = SessionCrypto.deriveSessionKey(pcPair, sessionId, CryptoDirection.PC_TO_ANDROID, keyId)
        val inputKey = SessionCrypto.deriveSessionKey(pcPair, sessionId, CryptoDirection.ANDROID_TO_PC, keyId)
        assertFalse(outputKey.contentEquals(inputKey))
        val nonce = SessionCrypto.nonce(byteArrayOf(1, 2, 3, 4), 9u)
        val plaintext = "\u001b[31mTermLink\u001b[0m".encodeToByteArray()
        val aad = "protobuf-fields-1-through-10".encodeToByteArray()
        assertArrayEquals(plaintext, SessionCrypto.decrypt(outputKey, nonce, aad, SessionCrypto.encrypt(outputKey, nonce, aad, plaintext)))
    }

    @Test
    fun deviceIdentitySignsWithEd25519() {
        val identity = DeviceIdentity.generate()
        assertFalse(identity.sign("proof".encodeToByteArray()).all { it == 0.toByte() })
    }

    @Test
    fun matchesCrossPlatformE2eeVector() {
        val pc = DeviceExchangeKeyPair(hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"), hex("8f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f"))
        val androidPublic = hex("358072d6365880d1aeea329adf9121383851ed21a28e3b75e965d0d2cd166254")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val pair = SessionCrypto.derivePairwiseKey(pc.privateKey, androidPublic, pcId, androidId, 7)
        assertArrayEquals(hex("b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773"), pair)
        val key = SessionCrypto.deriveSessionKey(pair, sessionId, CryptoDirection.PC_TO_ANDROID, "epoch-00000007".encodeToByteArray())
        assertArrayEquals(hex("e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c"), key)
        val nonce = hex("aabbccdd000000000000002a")
        val aad = hex("080110141a10fedcba9876543210ffffeeeeeeeeeeee221000112233445566778899aabbccddeeff2801302a380740fba0f1c2b1344a0e65706f63682d3030303030303037520caabbccdd000000000000002a")
        val plaintext = hex("1b5b33316d5465726d4c696e6b1b5b306d0d0a")
        assertArrayEquals(hex("6a97c75f0d8c984923467129d973a325134d69c9bd792587797651196a7498a68e3c44"), SessionCrypto.encrypt(key, nonce, aad, plaintext))
    }

    @Test
    fun opensCrossPlatformBoundedHistoryVector() {
        val pcPublic = hex("8f40c5adb68f25624ae5b214ea767a6ec94d829d3d7b5e1ad1ba6f3e2138285f")
        val androidPrivate = hex("202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f")
        val pcId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val androidId = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val pairwise = SessionCrypto.derivePairwiseKey(androidPrivate, pcPublic, pcId, androidId, 7)
        val sealedObject = hex(
            "08011210fedcba9876543210ffffeeeeeeeeeeee1a1000112233445566778899aabbccddeeff" +
                "2210102132435465768798a9bacbdcedfe0f28073234686973746f72792d76313a31303231" +
                "333234333534363537363837393861396261636264636564666530663a3030303030303037" +
                "3829402a4a0c000102030405060708090a0b522f7795cb6643e7868534311f82aff8381fcf" +
                "651b52f354c62b6929e248b47358486a0bba113af757d4dd62eede2d3df4",
        )

        val chunk = HistoryCrypto.open(
            pairwise,
            sealedObject,
            sessionId,
            pcId,
            androidId,
            7,
            HistoryCrypto.keyId(androidId, 7),
            41u,
            42u,
        )

        assertArrayEquals("history\r\n".encodeToByteArray(), OutputChunkPayload.parseFrom(chunk.eventsList[0].payload).output.toByteArray())
        val resize = ResizePayload.parseFrom(chunk.eventsList[1].payload)
        assertArrayEquals(intArrayOf(120, 40), intArrayOf(resize.columns, resize.rows))
    }

    private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}

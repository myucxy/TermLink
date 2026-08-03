package dev.termlink.crypto

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.UUID
import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.modes.ChaCha20Poly1305
import org.bouncycastle.crypto.params.AEADParameters
import org.bouncycastle.crypto.params.HKDFParameters
import org.bouncycastle.crypto.params.KeyParameter
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters

enum class CryptoDirection(val wireValue: Byte) { PC_TO_ANDROID(1), ANDROID_TO_PC(2) }

data class DeviceExchangeKeyPair(val privateKey: ByteArray, val publicKey: ByteArray) {
    init { require(privateKey.size == 32 && publicKey.size == 32) }
    companion object {
        fun generate(random: SecureRandom = SecureRandom()): DeviceExchangeKeyPair {
            val privateKey = X25519PrivateKeyParameters(random)
            return DeviceExchangeKeyPair(privateKey.encoded, privateKey.generatePublicKey().encoded)
        }
    }
}

object SessionCrypto {
    private val salt = sha256("TERMLINK-E2EE-V1".encodeToByteArray())

    fun derivePairwiseKey(
        privateKey: ByteArray,
        peerPublicKey: ByteArray,
        pcDeviceId: UUID,
        androidDeviceId: UUID,
        keyEpoch: Long,
    ): ByteArray {
        val agreement = X25519Agreement()
        agreement.init(X25519PrivateKeyParameters(privateKey, 0))
        val shared = ByteArray(agreement.agreementSize)
        agreement.calculateAgreement(X25519PublicKeyParameters(peerPublicKey, 0), shared, 0)
        val info = bytes(
            "TERMLINK-PAIRWISE-V1".encodeToByteArray(), byteArrayOf(0),
            uuidBytes(pcDeviceId), uuidBytes(androidDeviceId), longBytes(keyEpoch),
        )
        return hkdf(shared, salt, info)
    }

    fun deriveSessionKey(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        direction: CryptoDirection,
        keyId: ByteArray,
    ): ByteArray = hkdf(
        pairwiseKey,
        salt,
        bytes(
            "TERMLINK-SESSION-KEY-V1".encodeToByteArray(), byteArrayOf(0),
            uuidBytes(sessionId), byteArrayOf(direction.wireValue), keyId,
        ),
    )

    fun deriveHistoryKey(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: ByteArray,
    ): ByteArray {
        require(pairwiseKey.size == 32) { "Pairwise key must be 32 bytes" }
        require(keyEpoch > 0) { "Key epoch must be positive" }
        require(keyId.size in 8..128) { "Key ID length is invalid" }
        return hkdf(
            pairwiseKey,
            salt,
            bytes(
                "TERMLINK-HISTORY-KEY-V1".encodeToByteArray(), byteArrayOf(0),
                uuidBytes(sessionId), uuidBytes(recipientDeviceId), longBytes(keyEpoch), keyId,
            ),
        )
    }

    fun nonce(prefix: ByteArray, sequence: ULong): ByteArray {
        require(prefix.size == 4 && sequence > 0u)
        return bytes(prefix, ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(sequence.toLong()).array())
    }

    fun encrypt(key: ByteArray, nonce: ByteArray, associatedData: ByteArray, plaintext: ByteArray): ByteArray =
        process(true, key, nonce, associatedData, plaintext)

    fun decrypt(key: ByteArray, nonce: ByteArray, associatedData: ByteArray, ciphertext: ByteArray): ByteArray =
        process(false, key, nonce, associatedData, ciphertext)

    private fun process(encrypt: Boolean, key: ByteArray, nonce: ByteArray, aad: ByteArray, input: ByteArray): ByteArray {
        require(key.size == 32 && nonce.size == 12)
        val cipher = ChaCha20Poly1305()
        cipher.init(encrypt, AEADParameters(KeyParameter(key), 128, nonce, aad))
        val output = ByteArray(cipher.getOutputSize(input.size))
        val count = cipher.processBytes(input, 0, input.size, output, 0)
        val finalCount = cipher.doFinal(output, count)
        return output.copyOf(count + finalCount)
    }

    private fun hkdf(input: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
        val generator = HKDFBytesGenerator(SHA256Digest())
        generator.init(HKDFParameters(input, salt, info))
        return ByteArray(32).also { generator.generateBytes(it, 0, it.size) }
    }

    private fun sha256(input: ByteArray): ByteArray = SHA256Digest().run {
        update(input, 0, input.size)
        ByteArray(digestSize).also { doFinal(it, 0) }
    }

    internal fun uuidBytes(value: UUID): ByteArray = ByteBuffer.allocate(16).order(ByteOrder.BIG_ENDIAN)
        .putLong(value.mostSignificantBits).putLong(value.leastSignificantBits).array()
    internal fun longBytes(value: Long): ByteArray = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value).array()
    internal fun bytes(vararg values: ByteArray): ByteArray = values.fold(ByteArray(0)) { result, value -> result + value }
}

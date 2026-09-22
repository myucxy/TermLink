package dev.termlink.crypto

import java.security.SecureRandom
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer

data class DeviceIdentity(
    val signingPrivateKey: ByteArray,
    val signingPublicKey: ByteArray,
    val exchange: DeviceExchangeKeyPair,
) {
    init { require(signingPrivateKey.size == 32 && signingPublicKey.size == 32) }

    fun sign(message: ByteArray): ByteArray {
        val signer = Ed25519Signer()
        signer.init(true, Ed25519PrivateKeyParameters(signingPrivateKey, 0))
        signer.update(message, 0, message.size)
        return signer.generateSignature()
    }

    fun destroy() {
        signingPrivateKey.fill(0)
        exchange.privateKey.fill(0)
    }

    fun privateMaterial(): ByteArray = signingPrivateKey.copyOf() + exchange.privateKey.copyOf()

    companion object {
        fun verify(publicKey: ByteArray, message: ByteArray, signature: ByteArray): Boolean =
            runCatching {
                if (publicKey.size != 32 || signature.size != 64) return@runCatching false
                Ed25519Signer().run {
                    init(false, Ed25519PublicKeyParameters(publicKey, 0))
                    update(message, 0, message.size)
                    verifySignature(signature)
                }
            }.getOrDefault(false)

        fun generate(random: SecureRandom = SecureRandom()): DeviceIdentity {
            val signing = Ed25519PrivateKeyParameters(random)
            return DeviceIdentity(signing.encoded, signing.generatePublicKey().encoded, DeviceExchangeKeyPair.generate(random))
        }

        fun fromPrivateMaterial(material: ByteArray): DeviceIdentity {
            require(material.size == 64)
            val signing = Ed25519PrivateKeyParameters(material, 0)
            val exchange = org.bouncycastle.crypto.params.X25519PrivateKeyParameters(material, 32)
            return DeviceIdentity(
                signing.encoded,
                signing.generatePublicKey().encoded,
                DeviceExchangeKeyPair(exchange.encoded, exchange.generatePublicKey().encoded),
            )
        }
    }
}

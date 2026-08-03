package dev.termlink.network.production

import dev.termlink.crypto.DeviceIdentity
import java.security.MessageDigest
import java.security.SecureRandom

data class DeviceProofHeaders(
    val deviceId: String,
    val timestampMillis: Long,
    val nonce: String,
    val signature: String,
)

object DeviceProof {
    fun create(
        identity: DeviceIdentity,
        deviceId: String,
        method: String,
        requestTarget: String,
        body: ByteArray,
        nowMillis: Long = System.currentTimeMillis(),
        random: SecureRandom = SecureRandom(),
    ): DeviceProofHeaders {
        val nonceBytes = ByteArray(16).also(random::nextBytes)
        val nonce = base64Url(nonceBytes)
        val bodyHash = MessageDigest.getInstance("SHA-256").digest(body).toHex()
        val canonical = listOf(
            "TERMLINK-DEVICE-PROOF-V1",
            method.uppercase(),
            requestTarget,
            nowMillis.toString(),
            nonce,
            bodyHash,
        ).joinToString("\n").encodeToByteArray()
        return DeviceProofHeaders(deviceId, nowMillis, nonce, base64Url(identity.sign(canonical)))
    }

    internal fun base64Url(value: ByteArray): String = java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(value)
    internal fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}

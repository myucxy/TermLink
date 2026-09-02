package dev.termlink.network.direct

import dev.termlink.crypto.DeviceIdentity
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

internal data class DirectSignalingProofHeaders(
    val deviceId: String,
    val timestampMillis: Long,
    val nonce: String,
    val signature: String,
)

/** Canonical device proofs for direct HTTP control and the isolated signals socket. */
internal object DirectTransportSignalingProof {
    internal const val DEVICE_PROOF_DOMAIN = "TERMLINK-DEVICE-PROOF-V1"
    internal const val SIGNAL_PROOF_DOMAIN = "TERMLINK-TRANSPORT-SIGNAL-PROOF-V1"

    fun createDeviceProof(
        identity: DeviceIdentity,
        deviceId: String,
        method: String,
        requestTarget: String,
        body: ByteArray,
        nowMillis: Long,
        random: SecureRandom,
    ): DirectSignalingProofHeaders = create(
        DEVICE_PROOF_DOMAIN,
        identity,
        deviceId,
        method,
        requestTarget,
        body,
        nowMillis,
        random,
    )

    fun createSignalProof(
        identity: DeviceIdentity,
        deviceId: String,
        nowMillis: Long,
        random: SecureRandom,
    ): DirectSignalingProofHeaders = create(
        SIGNAL_PROOF_DOMAIN,
        identity,
        deviceId,
        "GET",
        DirectTransportSignalingClient.SIGNAL_PATH,
        ByteArray(0),
        nowMillis,
        random,
    )

    internal fun canonical(
        domain: String,
        method: String,
        requestTarget: String,
        timestampMillis: Long,
        nonce: String,
        body: ByteArray,
    ): ByteArray = listOf(
        domain,
        method.uppercase(),
        requestTarget,
        timestampMillis.toString(),
        nonce,
        MessageDigest.getInstance("SHA-256").digest(body).toHex(),
    ).joinToString("\n").encodeToByteArray()

    private fun create(
        domain: String,
        identity: DeviceIdentity,
        deviceId: String,
        method: String,
        requestTarget: String,
        body: ByteArray,
        nowMillis: Long,
        random: SecureRandom,
    ): DirectSignalingProofHeaders {
        val nonce = ByteArray(NONCE_BYTES).also(random::nextBytes).base64Url()
        val canonical = canonical(domain, method, requestTarget, nowMillis, nonce, body)
        return DirectSignalingProofHeaders(
            deviceId = deviceId,
            timestampMillis = nowMillis,
            nonce = nonce,
            signature = identity.sign(canonical).base64Url(),
        )
    }

    internal fun ByteArray.base64Url(): String = Base64.getUrlEncoder().withoutPadding().encodeToString(this)
    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }

    private const val NONCE_BYTES = 16
}

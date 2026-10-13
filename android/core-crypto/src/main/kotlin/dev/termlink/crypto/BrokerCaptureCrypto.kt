package dev.termlink.crypto

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters

/** One authenticated path-free TLCAPV1 capture. The caller clears returned plaintext after decoding. */
object BrokerCaptureCrypto {
    const val HEADER_BYTES = 192
    const val MAXIMUM_CANONICAL_BYTES = 32 * 1024 * 1024
    const val MAXIMUM_OBJECT_BYTES = MAXIMUM_CANONICAL_BYTES + HEADER_BYTES + 16
    private const val MINIMUM_CANONICAL_BYTES = 136
    private val magic = "TLBCSEA1".encodeToByteArray()
    private val nativeMagic = "TLCAPV1\u0000".encodeToByteArray()
    const val STATE_CONTRACT_V1 = "terminal_state_2_22_v1"
    const val STATE_CONTRACT_V2 = "terminal_state_2_22_v2"
    private val stateContractV1 = STATE_CONTRACT_V1.encodeToByteArray()
    private val stateContractV2 = STATE_CONTRACT_V2.encodeToByteArray()
    private val aadPrefixV1 = "TermLink broker capture AAD V1\n".encodeToByteArray()
    private val aadPrefixV2 = "TermLink broker capture AAD V2\n".encodeToByteArray()
    private val keyPrefixV1 = "TermLink broker capture key V1\n".encodeToByteArray()
    private val keyPrefixV2 = "TermLink broker capture key V2\n".encodeToByteArray()
    private val emptyUuid = UUID(0, 0)

    fun keyId(recipientDeviceId: UUID, keyEpoch: Long, stateContract: String = STATE_CONTRACT_V1): String {
        require(recipientDeviceId != emptyUuid && keyEpoch > 0) { "Broker capture key identity is invalid" }
        val variant = when (normalizeStateContract(stateContract)) { STATE_CONTRACT_V1 -> "v1"; else -> "v2" }
        return "broker_capture_$variant:${recipientDeviceId.toString().replace("-", "")}:$keyEpoch"
    }

    /** Explicit nonce is for fixed cross-platform vectors; production uses a random nonce. */
    fun seal(pairwiseKey: ByteArray, sessionId: UUID, pcDeviceId: UUID,
        recipientDeviceId: UUID, objectId: UUID, sourceSequence: ULong, keyEpoch: Long,
        canonical: ByteArray, nonce: ByteArray? = null,
        stateContract: String = STATE_CONTRACT_V1): ByteArray {
        validateIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId,
            objectId, sourceSequence, keyEpoch)
        require(canonical.size in MINIMUM_CANONICAL_BYTES..MAXIMUM_CANONICAL_BYTES) {
            "Broker capture canonical length is invalid"
        }
        validateCanonical(canonical, sourceSequence)
        require(nonce == null || nonce.size == 12) { "Broker capture nonce must be 12 bytes" }
        val nonceBytes = nonce?.copyOf() ?: ByteArray(12).also { SecureRandom().nextBytes(it) }
        val header = ByteArray(HEADER_BYTES)
        val digest = MessageDigest.getInstance("SHA-256").digest(canonical)
        var key: ByteArray? = null
        var aad: ByteArray? = null
        try {
            val view = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN)
            magic.copyInto(header, 0)
            view.putShort(8, 1)
            view.putShort(10, HEADER_BYTES.toShort())
            SessionCrypto.uuidBytes(sessionId).copyInto(header, 12)
            SessionCrypto.uuidBytes(pcDeviceId).copyInto(header, 28)
            SessionCrypto.uuidBytes(recipientDeviceId).copyInto(header, 44)
            SessionCrypto.uuidBytes(objectId).copyInto(header, 60)
            view.putLong(76, sourceSequence.toLong())
            view.putLong(84, keyEpoch)
            view.putInt(92, canonical.size)
            digest.copyInto(header, 96)
            nonceBytes.copyInto(header, 128)
            stateContractBytes(stateContract).copyInto(header, 140)
            key = deriveKey(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch, stateContract)
            aad = associatedData(header, stateContract)
            val result = ByteArray(HEADER_BYTES + canonical.size + 16)
            try {
                header.copyInto(result)
                Cipher.getInstance("AES/GCM/NoPadding").run {
                    init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonceBytes))
                    updateAAD(aad)
                    require(doFinal(ByteBuffer.wrap(canonical), ByteBuffer.wrap(result, HEADER_BYTES,
                        canonical.size + 16)) == canonical.size + 16) {
                        "Broker capture ciphertext length is invalid"
                    }
                }
                return result
            } catch (error: Throwable) {
                result.fill(0)
                throw error
            }
        } finally {
            header.fill(0)
            digest.fill(0)
            nonceBytes.fill(0)
            key?.fill(0)
            aad?.fill(0)
        }
    }

    /** Returns owned canonical bytes; caller must clear after native decoding. */
    fun open(pairwiseKey: ByteArray, sealedObject: ByteArray, expectedSessionId: UUID,
        expectedPcDeviceId: UUID, expectedRecipientDeviceId: UUID, expectedObjectId: UUID,
        expectedSourceSequence: ULong, expectedKeyEpoch: Long, expectedCanonicalLength: Int,
        expectedCanonicalSha256: ByteArray,
        expectedStateContract: String = STATE_CONTRACT_V1): ByteArray {
        validateIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId,
            expectedRecipientDeviceId, expectedObjectId, expectedSourceSequence, expectedKeyEpoch)
        require(expectedCanonicalLength in MINIMUM_CANONICAL_BYTES..MAXIMUM_CANONICAL_BYTES &&
            expectedCanonicalSha256.size == 32 &&
            sealedObject.size == HEADER_BYTES + expectedCanonicalLength + 16 &&
            sealedObject.size <= MAXIMUM_OBJECT_BYTES) { "Broker capture expected object size or digest is invalid" }
        val header = sealedObject.copyOfRange(0, HEADER_BYTES)
        val view = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN)
        try {
            require(header.matches(0, magic) && view.getShort(8).toInt() == 1 &&
                view.getShort(10).toInt() == HEADER_BYTES &&
                header.matches(12, SessionCrypto.uuidBytes(expectedSessionId)) &&
                header.matches(28, SessionCrypto.uuidBytes(expectedPcDeviceId)) &&
                header.matches(44, SessionCrypto.uuidBytes(expectedRecipientDeviceId)) &&
                header.matches(60, SessionCrypto.uuidBytes(expectedObjectId)) &&
                view.getLong(76).toULong() == expectedSourceSequence &&
                view.getLong(84) == expectedKeyEpoch && view.getInt(92) == expectedCanonicalLength &&
                MessageDigest.isEqual(header.copyOfRange(96, 128), expectedCanonicalSha256) &&
                header.matches(140, stateContractBytes(expectedStateContract)) &&
                (140 + stateContractBytes(expectedStateContract).size until HEADER_BYTES).all { header[it] == 0.toByte() }) {
                "Broker capture metadata does not match expected identity"
            }
            val key = deriveKey(pairwiseKey, expectedSessionId, expectedPcDeviceId,
                expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract)
            val aad = associatedData(header, expectedStateContract)
            val nonce = header.copyOfRange(128, 140)
            val canonical = ByteArray(expectedCanonicalLength)
            try {
                Cipher.getInstance("AES/GCM/NoPadding").run {
                    init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
                    updateAAD(aad)
                    require(doFinal(ByteBuffer.wrap(sealedObject, HEADER_BYTES,
                        expectedCanonicalLength + 16), ByteBuffer.wrap(canonical)) == expectedCanonicalLength) {
                        "Broker capture plaintext length is invalid"
                    }
                }
                val digest = MessageDigest.getInstance("SHA-256").digest(canonical)
                try {
                    require(MessageDigest.isEqual(digest, expectedCanonicalSha256)) {
                        "Broker capture canonical digest differs from its container"
                    }
                } finally { digest.fill(0) }
                validateCanonical(canonical, expectedSourceSequence)
                return canonical
            } catch (error: Throwable) {
                canonical.fill(0)
                throw error
            } finally {
                key.fill(0)
                aad.fill(0)
                nonce.fill(0)
            }
        } finally { header.fill(0) }
    }

    internal fun associatedData(header: ByteArray): ByteArray = associatedData(header, STATE_CONTRACT_V1)

    internal fun associatedData(header: ByteArray, stateContract: String): ByteArray {
        require(header.size == HEADER_BYTES) { "Broker capture header size is invalid" }
        return aadPrefix(stateContract) + header
    }

    private fun validateIdentity(key: ByteArray, session: UUID, pc: UUID, recipient: UUID,
        objectId: UUID, sequence: ULong, epoch: Long) {
        require(key.size == 32 && session != emptyUuid && pc != emptyUuid &&
            recipient != emptyUuid && recipient != pc && objectId != emptyUuid &&
            sequence > 0uL && sequence < Long.MAX_VALUE.toULong() && epoch > 0) {
            "Broker capture identity is invalid"
        }
    }

    private fun validateCanonical(canonical: ByteArray, sequence: ULong) {
        require(canonical.size in MINIMUM_CANONICAL_BYTES..MAXIMUM_CANONICAL_BYTES) {
            "Broker capture canonical length is invalid"
        }
        val view = ByteBuffer.wrap(canonical).order(ByteOrder.LITTLE_ENDIAN)
        require(canonical.matches(0, nativeMagic) && view.getInt(8) == 32 &&
            view.getInt(12) in 1..64 && view.getLong(16).toULong() == sequence &&
            canonical.size >= 32 + 104 * view.getInt(12) &&
            view.getInt(24) == canonical.size && view.getInt(28) == 0) {
            "Broker capture TLCAPV1 header is invalid"
        }
    }

    private fun deriveKey(pairwiseKey: ByteArray, session: UUID, pc: UUID,
        recipient: UUID, epoch: Long, stateContract: String): ByteArray {
        val prefix = keyPrefix(stateContract)
        val info = prefix + SessionCrypto.uuidBytes(pc) + SessionCrypto.uuidBytes(recipient) +
            ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(epoch).array()
        return try {
            val hkdf = HKDFBytesGenerator(SHA256Digest())
            hkdf.init(HKDFParameters(pairwiseKey, SessionCrypto.uuidBytes(session), info))
            ByteArray(32).also { hkdf.generateBytes(it, 0, it.size) }
        } finally { info.fill(0) }
    }

    private fun normalizeStateContract(value: String): String = when (value) {
        STATE_CONTRACT_V1, STATE_CONTRACT_V2 -> value
        else -> error("Broker capture state contract is unsupported")
    }
    private fun stateContractBytes(value: String): ByteArray =
        if (normalizeStateContract(value) == STATE_CONTRACT_V1) stateContractV1 else stateContractV2
    private fun aadPrefix(value: String): ByteArray =
        if (normalizeStateContract(value) == STATE_CONTRACT_V1) aadPrefixV1 else aadPrefixV2
    private fun keyPrefix(value: String): ByteArray =
        if (normalizeStateContract(value) == STATE_CONTRACT_V1) keyPrefixV1 else keyPrefixV2

    private fun ByteArray.matches(offset: Int, expected: ByteArray): Boolean =
        expected.indices.all { this[offset + it] == expected[it] }
}

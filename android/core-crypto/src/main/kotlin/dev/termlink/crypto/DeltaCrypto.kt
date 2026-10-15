package dev.termlink.crypto

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters

/** One authenticated native TLDELTA1 object. The caller clears returned plaintext after native apply. */
data class OpenedDelta(val delta: ByteArray, val targetObservedLocalInputEpoch: ULong,
    val targetInitialTranscriptBeforeLineId: ULong)

object DeltaCrypto {
    const val HEADER_BYTES = 336
    const val MAXIMUM_OBJECT_BYTES = 8 * 1024 * 1024
    const val MAXIMUM_PLAINTEXT_BYTES = MAXIMUM_OBJECT_BYTES - HEADER_BYTES - 16
    const val FORMAT_NAME = "TERMLINK_STATE_DELTA_V1"
    const val FORMAT_VERSION = 1
    const val STATE_CONTRACT_V1 = "terminal_state_2_22_v1"
    const val STATE_CONTRACT_V2 = "terminal_state_2_22_v2"
    private val magic = "TLDSEAL1".encodeToByteArray()
    private val nativeMagic = "TLDELTA1".encodeToByteArray()
    private val keyPrefixV1 = "TERMLINK-STATE-DELTA-KEY-V1\u0000".encodeToByteArray()
    private val keyPrefixV2 = "TERMLINK-STATE-DELTA-KEY-V2\u0000".encodeToByteArray()
    private val aadPrefixV1 = "TERMLINK-STATE-DELTA-OBJECT-V1\u0000".encodeToByteArray()
    private val aadPrefixV2 = "TERMLINK-STATE-DELTA-OBJECT-V2\u0000".encodeToByteArray()
    private val contextPrefixV1 = "TermLink state delta context V1\n".encodeToByteArray()
    private val contextPrefixV2 = "TermLink state delta context V2\n".encodeToByteArray()

    fun deriveContextId(sessionId: UUID, pcDeviceId: UUID, stateContract: String): ByteArray {
        val normalized = normalizeStateContract(stateContract)
        require(sessionId != UUID(0, 0) && pcDeviceId != UUID(0, 0)) {
            "Delta context identity is invalid"
        }
        val prefix = if (normalized == STATE_CONTRACT_V1) contextPrefixV1 else contextPrefixV2
        val material = prefix + SessionCrypto.uuidBytes(sessionId) +
            SessionCrypto.uuidBytes(pcDeviceId) + stateContract.encodeToByteArray()
        return try { MessageDigest.getInstance("SHA-256").digest(material) }
        finally { material.fill(0) }
    }

    fun keyId(recipientDeviceId: UUID, keyEpoch: Long, stateContract: String = STATE_CONTRACT_V1): String {
        require(recipientDeviceId != UUID(0, 0) && keyEpoch > 0) { "Delta key identity is invalid" }
        val variant = if (normalizeStateContract(stateContract) == STATE_CONTRACT_V2) "v2" else "v1"
        return "delta-$variant:${recipientDeviceId.toString().replace("-", "")}:${keyEpoch.toString(16)}"
    }

    /** The caller owns and must clear the returned TLDELTA1 byte array after native apply. */
    fun open(pairwiseKey: ByteArray, sealedObject: ByteArray, expectedSessionId: UUID,
        expectedPcDeviceId: UUID, expectedRecipientDeviceId: UUID, expectedKeyEpoch: Long,
        expectedStateContract: String, expectedContextId: ByteArray, expectedProfile: Int,
        expectedBaseNextSequence: ULong, expectedTargetNextSequence: ULong,
        expectedBaseSnapshotBytes: ULong, expectedTargetSnapshotBytes: ULong,
        expectedBaseSha256: ByteArray, expectedTargetSha256: ByteArray,
        minimumObservedLocalInputEpoch: ULong): OpenedDelta {
        validateExpected(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId,
            expectedKeyEpoch, expectedStateContract, expectedContextId, expectedProfile,
            expectedBaseNextSequence, expectedTargetNextSequence, expectedBaseSnapshotBytes,
            expectedTargetSnapshotBytes, expectedBaseSha256, expectedTargetSha256)
        require(minimumObservedLocalInputEpoch <= Long.MAX_VALUE.toULong()) { "Delta minimum local input epoch is invalid" }
        require(sealedObject.size in HEADER_BYTES + 256 + 16..MAXIMUM_OBJECT_BYTES) {
            "Sealed Delta object size is invalid"
        }
        val header = sealedObject.copyOfRange(0, HEADER_BYTES)
        validateHeader(header, sealedObject.size, expectedSessionId, expectedPcDeviceId,
            expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract, expectedContextId,
            expectedProfile, expectedBaseNextSequence, expectedTargetNextSequence,
            expectedBaseSnapshotBytes, expectedTargetSnapshotBytes, expectedBaseSha256, expectedTargetSha256)
        val targetEpoch = ByteBuffer.wrap(header, 306, 8).order(ByteOrder.BIG_ENDIAN).long.toULong()
        val targetAnchor = ByteBuffer.wrap(header, 314, 8).order(ByteOrder.BIG_ENDIAN).long.toULong()
        validateTargetMetadata(targetEpoch, targetAnchor, minimumObservedLocalInputEpoch)
        val key = deriveKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract)
        val aad = associatedData(header, expectedStateContract)
        val nonce = header.copyOfRange(294, 306)
        val plaintext = ByteArray(sealedObject.size - HEADER_BYTES - 16)
        try {
            Cipher.getInstance("AES/GCM/NoPadding").run {
                init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
                updateAAD(aad)
                // Android 9's ByteBuffer adapter can demand ciphertext-sized output for GCM decryption.
                require(doFinal(sealedObject, HEADER_BYTES, sealedObject.size - HEADER_BYTES,
                    plaintext) == plaintext.size) { "Delta plaintext length is invalid" }
            }
            validateNative(plaintext, expectedContextId, expectedProfile, expectedBaseNextSequence,
                expectedTargetNextSequence, expectedBaseSnapshotBytes, expectedTargetSnapshotBytes,
                expectedBaseSha256, expectedTargetSha256)
            return OpenedDelta(plaintext, targetEpoch, targetAnchor)
        } catch (error: Throwable) {
            plaintext.fill(0)
            throw error
        } finally {
            key.fill(0)
            aad.fill(0)
            nonce.fill(0)
            header.fill(0)
        }
    }

    internal fun associatedData(header: ByteArray): ByteArray = associatedData(header, STATE_CONTRACT_V1)

    internal fun associatedData(header: ByteArray, stateContract: String): ByteArray {
        require(header.size == HEADER_BYTES) { "Delta header size is invalid" }
        return (if (normalizeStateContract(stateContract) == STATE_CONTRACT_V1) aadPrefixV1 else aadPrefixV2) + header
    }

    private fun validateHeader(header: ByteArray, objectBytes: Int, session: UUID, pc: UUID,
        recipient: UUID, epoch: Long, state: String, context: ByteArray, profile: Int,
        baseSeq: ULong, targetSeq: ULong, baseBytes: ULong, targetBytes: ULong,
        baseHash: ByteArray, targetHash: ByteArray) {
        val view = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN)
        require(header.copyOfRange(0, 8).contentEquals(magic) &&
            view.getShort(8).toInt() == 1 && view.getShort(10).toInt() == HEADER_BYTES &&
            header.matches(12, SessionCrypto.uuidBytes(session)) &&
            header.matches(28, SessionCrypto.uuidBytes(pc)) &&
            header.matches(44, SessionCrypto.uuidBytes(recipient)) &&
            view.getLong(60) == epoch &&
            matchesAscii(header, 68, 64, keyId(recipient, epoch, state)) &&
            matchesAscii(header, 133, 24, state) && header.matches(158, context) &&
            view.getInt(190) == profile && view.getLong(194).toULong() == baseSeq &&
            view.getLong(202).toULong() == targetSeq && view.getLong(210).toULong() == baseBytes &&
            view.getLong(218).toULong() == targetBytes && header.matches(226, baseHash) &&
            header.matches(258, targetHash) && view.getInt(290) == objectBytes - HEADER_BYTES - 16 &&
            (322 until HEADER_BYTES).all { header[it] == 0.toByte() }) {
            "Delta object metadata does not match the requested transition"
        }
    }

    private fun validateNative(delta: ByteArray, context: ByteArray, profile: Int, baseSeq: ULong,
        targetSeq: ULong, baseBytes: ULong, targetBytes: ULong,
        baseHash: ByteArray, targetHash: ByteArray) {
        require(delta.size in 256..MAXIMUM_PLAINTEXT_BYTES) { "Native Delta size is invalid" }
        val view = ByteBuffer.wrap(delta).order(ByteOrder.LITTLE_ENDIAN)
        require(delta.copyOfRange(0, 8).contentEquals(nativeMagic) &&
            view.getShort(8).toInt() == 1 && view.getShort(10).toInt() == 256 &&
            view.getInt(12) == 4096 && view.getInt(16) == profile && view.getInt(20) == 1 &&
            delta.matches(24, context) && view.getLong(56).toULong() == baseSeq &&
            view.getLong(64).toULong() == targetSeq && view.getLong(72).toULong() == baseBytes &&
            view.getLong(80).toULong() == targetBytes && delta.matches(88, baseHash) &&
            delta.matches(120, targetHash) && view.getLong(192) == delta.size.toLong() &&
            targetBytes > delta.size.toULong()) {
            "Native Delta header does not match the authenticated transition"
        }
    }

    private fun validateExpected(key: ByteArray, session: UUID, pc: UUID, recipient: UUID,
        epoch: Long, state: String, context: ByteArray, profile: Int, baseSeq: ULong,
        targetSeq: ULong, baseBytes: ULong, targetBytes: ULong,
        baseHash: ByteArray, targetHash: ByteArray) {
        require(key.size == 32 && session != UUID(0, 0) && pc != UUID(0, 0) &&
            recipient != UUID(0, 0) && pc != recipient && epoch > 0 && normalizeStateContract(state) == state &&
            context.size == 32 && MessageDigest.isEqual(context, deriveContextId(session, pc, state)) &&
            profile in 1..4 &&
            (if (state == STATE_CONTRACT_V1) profile == 1 || profile == 3 else profile == 2 || profile == 4) &&
            baseSeq > 0uL && targetSeq > baseSeq &&
            baseBytes in 104uL..(128uL * 1024uL * 1024uL) &&
            targetBytes in 104uL..(128uL * 1024uL * 1024uL) &&
            baseHash.size == 32 && targetHash.size == 32) { "Delta transition identity is invalid" }
    }

    private fun validateTargetMetadata(targetEpoch: ULong, targetAnchor: ULong, minimumEpoch: ULong) {
        require(targetEpoch <= Long.MAX_VALUE.toULong() && targetEpoch >= minimumEpoch && targetAnchor == 1uL) {
            "Delta target local input epoch or history anchor is invalid"
        }
    }

    private fun deriveKey(pairwiseKey: ByteArray, session: UUID, recipient: UUID, epoch: Long,
        stateContract: String): ByteArray {
        val keyIdBytes = keyId(recipient, epoch, stateContract).encodeToByteArray()
        val info = ByteArrayOutputStream().use { output ->
            output.write(if (normalizeStateContract(stateContract) == STATE_CONTRACT_V1) keyPrefixV1 else keyPrefixV2)
            output.write(SessionCrypto.uuidBytes(recipient))
            output.write(ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(epoch).array())
            output.write(keyIdBytes.size)
            output.write(keyIdBytes)
            output.toByteArray()
        }
        return try {
            val generator = HKDFBytesGenerator(SHA256Digest())
            generator.init(HKDFParameters(pairwiseKey, SessionCrypto.uuidBytes(session), info))
            ByteArray(32).also { generator.generateBytes(it, 0, it.size) }
        } finally {
            keyIdBytes.fill(0)
            info.fill(0)
        }
    }

    private fun normalizeStateContract(value: String): String = when (value) {
        STATE_CONTRACT_V1, STATE_CONTRACT_V2 -> value
        else -> error("Delta state contract is unsupported")
    }

    private fun ByteArray.matches(offset: Int, expected: ByteArray): Boolean =
        expected.indices.all { this[offset + it] == expected[it] }

    private fun matchesAscii(bytes: ByteArray, lengthAt: Int, width: Int, expected: String): Boolean {
        val encoded = expected.encodeToByteArray()
        return encoded.size <= width && bytes[lengthAt].toInt() == encoded.size &&
            bytes.matches(lengthAt + 1, encoded) &&
            (lengthAt + 1 + encoded.size until lengthAt + 1 + width).all { bytes[it] == 0.toByte() }
    }
}

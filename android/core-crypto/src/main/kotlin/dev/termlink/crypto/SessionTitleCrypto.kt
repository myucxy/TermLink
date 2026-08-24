package dev.termlink.crypto

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.util.UUID

object SessionTitleCrypto {
    const val FORMAT_VERSION = 1
    const val MAX_TITLE_UTF8_BYTES = 4_096
    const val MAX_ENVELOPE_BYTES = 1_048_576
    const val MAX_RECIPIENTS = 4_096

    private const val TAG_BYTES = 16
    private const val NONCE_BYTES = 12
    private const val HEADER_BYTES = 8 + 4 + 16 + 16 + 4
    private const val ENTRY_HEADER_BYTES = 16 + 8 + NONCE_BYTES + 4
    private val magic = "TLTITLE1".encodeToByteArray()
    private val aadPrefix = "TERMLINK-SESSION-TITLE-AAD-V1\u0000".encodeToByteArray()
    private val zeroUuid = UUID(0, 0)

    fun keyId(recipientDeviceId: UUID, keyEpoch: Long): String {
        require(recipientDeviceId != zeroUuid && keyEpoch > 0) { "Session title recipient metadata is invalid" }
        return "title-v1:${recipientDeviceId.toString().replace("-", "")}:${keyEpoch.toString().padStart(8, '0')}"
    }

    fun open(
        pairwiseKey: ByteArray,
        sealedEnvelope: ByteArray,
        expectedSessionId: UUID,
        expectedPcDeviceId: UUID,
        expectedRecipientDeviceId: UUID,
        expectedKeyEpoch: Long,
    ): String {
        require(pairwiseKey.size == 32) { "Pairwise key must be 32 bytes" }
        require(expectedSessionId != zeroUuid && expectedPcDeviceId != zeroUuid) {
            "Session title identity metadata is invalid"
        }
        require(
            expectedRecipientDeviceId != zeroUuid && expectedRecipientDeviceId != expectedPcDeviceId &&
                expectedKeyEpoch > 0,
        ) { "Session title recipient metadata is invalid" }
        require(
            sealedEnvelope.size in (HEADER_BYTES + ENTRY_HEADER_BYTES + TAG_BYTES)..MAX_ENVELOPE_BYTES,
        ) { "Session title envelope size is invalid" }

        val reader = EnvelopeReader(sealedEnvelope)
        require(reader.readBytes(magic.size).contentEquals(magic)) { "Session title envelope magic is invalid" }
        require(reader.readUInt() == FORMAT_VERSION.toUInt()) { "Session title envelope version is unsupported" }
        val sessionId = uuid(reader.readBytes(16))
        val pcDeviceId = uuid(reader.readBytes(16))
        require(sessionId == expectedSessionId && pcDeviceId == expectedPcDeviceId) {
            "Session title envelope identity does not match the request"
        }
        val recipientCount = reader.readUInt()
        require(recipientCount in 1u..MAX_RECIPIENTS.toUInt()) { "Session title recipient count is invalid" }

        val recipientIds = HashSet<UUID>(recipientCount.toInt())
        var matchingNonce: ByteArray? = null
        var matchingCiphertext: ByteArray? = null
        var matchingIndex = 0u
        var matchingEpoch = 0L
        var commonPlaintextLength: Int? = null
        try {
            repeat(recipientCount.toInt()) { rawIndex ->
                val recipientId = uuid(reader.readBytes(16))
                val keyEpoch = reader.readLong()
                val nonce = reader.readBytes(NONCE_BYTES)
                val ciphertextLength = reader.readUInt().toLong()
                require(
                    recipientId != zeroUuid && recipientId != pcDeviceId && keyEpoch > 0 &&
                        ciphertextLength in TAG_BYTES.toLong()..(MAX_TITLE_UTF8_BYTES + TAG_BYTES).toLong(),
                ) { "Session title recipient metadata is invalid" }
                require(recipientIds.add(recipientId)) { "Session title envelope contains a duplicate recipient" }
                val plaintextLength = ciphertextLength.toInt() - TAG_BYTES
                require(commonPlaintextLength == null || commonPlaintextLength == plaintextLength) {
                    "Session title entries have inconsistent plaintext lengths"
                }
                commonPlaintextLength = plaintextLength
                val ciphertext = reader.readBytes(ciphertextLength.toInt())

                if (recipientId == expectedRecipientDeviceId) {
                    matchingIndex = rawIndex.toUInt()
                    matchingEpoch = keyEpoch
                    matchingNonce = nonce
                    matchingCiphertext = ciphertext
                }
            }
            require(reader.atEnd) { "Session title envelope has trailing data" }
            val nonce = requireNotNull(matchingNonce) { "Session title envelope has no entry for this recipient" }
            val ciphertext = requireNotNull(matchingCiphertext) { "Session title envelope has no entry for this recipient" }
            require(matchingEpoch == expectedKeyEpoch) { "Session title key epoch does not match the request" }

            val keyIdBytes = strictUtf8(keyId(expectedRecipientDeviceId, expectedKeyEpoch))
            val key = SessionCrypto.deriveHistoryKey(
                pairwiseKey,
                expectedSessionId,
                expectedRecipientDeviceId,
                expectedKeyEpoch,
                keyIdBytes,
            )
            val aad = associatedData(
                expectedSessionId,
                expectedPcDeviceId,
                recipientCount,
                matchingIndex,
                expectedRecipientDeviceId,
                expectedKeyEpoch,
                keyIdBytes,
                nonce,
                ciphertext.size - TAG_BYTES,
            )
            val plaintext = try {
                SessionCrypto.decrypt(key, nonce, aad, ciphertext)
            } catch (error: Exception) {
                throw IllegalArgumentException("Session title authentication failed", error)
            } finally {
                keyIdBytes.fill(0)
                key.fill(0)
                aad.fill(0)
            }
            return try {
                require(plaintext.size <= MAX_TITLE_UTF8_BYTES) { "Session title plaintext exceeds the size limit" }
                strictUtf8String(plaintext)
            } finally {
                plaintext.fill(0)
            }
        } finally {
            matchingNonce?.fill(0)
            matchingCiphertext?.fill(0)
        }
    }

    private fun associatedData(
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientCount: UInt,
        recipientIndex: UInt,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: ByteArray,
        nonce: ByteArray,
        plaintextLength: Int,
    ): ByteArray = ByteArrayOutputStream().use { output ->
        output.write(aadPrefix)
        output.write(uintBytes(FORMAT_VERSION.toUInt()))
        output.write(SessionCrypto.uuidBytes(sessionId))
        output.write(SessionCrypto.uuidBytes(pcDeviceId))
        output.write(uintBytes(recipientCount))
        output.write(uintBytes(recipientIndex))
        output.write(SessionCrypto.uuidBytes(recipientDeviceId))
        output.write(SessionCrypto.longBytes(keyEpoch))
        output.write(ushortBytes(keyId.size))
        output.write(keyId)
        output.write(nonce)
        output.write(uintBytes(plaintextLength.toUInt()))
        output.toByteArray()
    }

    private fun strictUtf8(value: String): ByteArray = try {
        val encoded = Charsets.UTF_8.newEncoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .encode(java.nio.CharBuffer.wrap(value))
        ByteArray(encoded.remaining()).also(encoded::get)
    } catch (error: Exception) {
        throw IllegalArgumentException("Session title is not valid Unicode", error)
    }

    private fun strictUtf8String(value: ByteArray): String = try {
        Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(value))
            .toString()
    } catch (error: Exception) {
        throw IllegalArgumentException("Session title plaintext is not valid UTF-8", error)
    }

    private fun uuid(value: ByteArray): UUID {
        require(value.size == 16) { "UUID must be 16 bytes" }
        return ByteBuffer.wrap(value).order(ByteOrder.BIG_ENDIAN).let { UUID(it.long, it.long) }
    }

    private fun ushortBytes(value: Int): ByteArray =
        ByteBuffer.allocate(2).order(ByteOrder.BIG_ENDIAN).putShort(value.toShort()).array()

    private fun uintBytes(value: UInt): ByteArray =
        ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(value.toInt()).array()

    private class EnvelopeReader(private val value: ByteArray) {
        private var offset = 0
        val atEnd: Boolean get() = offset == value.size

        fun readBytes(length: Int): ByteArray {
            require(length >= 0 && offset <= value.size - length) { "Session title envelope is truncated" }
            return value.copyOfRange(offset, offset + length).also { offset += length }
        }

        fun readUInt(): UInt = ByteBuffer.wrap(readBytes(4)).order(ByteOrder.BIG_ENDIAN).int.toUInt()
        fun readLong(): Long = ByteBuffer.wrap(readBytes(8)).order(ByteOrder.BIG_ENDIAN).long
    }
}

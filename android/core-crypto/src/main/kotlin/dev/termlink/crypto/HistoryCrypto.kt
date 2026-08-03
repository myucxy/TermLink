package dev.termlink.crypto

import dev.termlink.protocol.realtime.v1.HistoryChunkV1
import dev.termlink.protocol.realtime.v1.HistoryCiphertextV1
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

object HistoryCrypto {
    const val FORMAT_VERSION = 1
    private val aadPrefix = "TERMLINK-HISTORY-OBJECT-V1\u0000".encodeToByteArray()

    fun keyId(recipientDeviceId: UUID, keyEpoch: Long): String {
        require(keyEpoch > 0) { "Key epoch must be positive" }
        return "history-v1:${recipientDeviceId.toString().replace("-", "")}:${keyEpoch.toString().padStart(8, '0')}"
    }

    fun open(
        pairwiseKey: ByteArray,
        sealedObject: ByteArray,
        expectedSessionId: UUID,
        expectedPcDeviceId: UUID,
        expectedRecipientDeviceId: UUID,
        expectedKeyEpoch: Long,
        expectedKeyId: String,
        expectedStartSequence: ULong,
        expectedEndSequence: ULong,
    ): HistoryChunkV1 {
        val container = HistoryCiphertextV1.parseFrom(sealedObject)
        require(container.formatVersion == FORMAT_VERSION) { "Unsupported history object format" }
        require(container.sessionId.toByteArray().contentEquals(SessionCrypto.uuidBytes(expectedSessionId))) { "History session mismatch" }
        require(container.pcDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(expectedPcDeviceId))) { "History PC mismatch" }
        require(container.recipientDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(expectedRecipientDeviceId))) { "History recipient mismatch" }
        require(container.keyEpoch == expectedKeyEpoch && container.keyId == expectedKeyId) { "History key metadata mismatch" }
        require(container.startSeq.toULong() == expectedStartSequence && container.endSeq.toULong() == expectedEndSequence) { "History sequence range mismatch" }
        require(expectedStartSequence > 0u && expectedEndSequence >= expectedStartSequence) { "Invalid history sequence range" }
        require(container.nonce.size() == 12 && container.ciphertext.size() >= 16) { "Invalid history cryptographic metadata" }
        val keyIdBytes = expectedKeyId.encodeToByteArray()
        val key = SessionCrypto.deriveHistoryKey(
            pairwiseKey,
            expectedSessionId,
            expectedRecipientDeviceId,
            expectedKeyEpoch,
            keyIdBytes,
        )
        val aad = associatedData(container)
        val plaintext = try {
            SessionCrypto.decrypt(key, container.nonce.toByteArray(), aad, container.ciphertext.toByteArray())
        } finally {
            keyIdBytes.fill(0)
            key.fill(0)
            aad.fill(0)
        }
        return try {
            HistoryChunkV1.parseFrom(plaintext).also { chunk ->
                require(chunk.eventsCount > 0) { "History chunk is empty" }
                var expected = expectedStartSequence
                chunk.eventsList.forEach { event ->
                    require(event.seq.toULong() == expected && event.messageTypeValue != 0) { "History events are not contiguous" }
                    expected++
                }
                require(chunk.eventsList.last().seq.toULong() == expectedEndSequence) { "History event range mismatch" }
            }
        } finally {
            plaintext.fill(0)
        }
    }

    internal fun associatedData(value: HistoryCiphertextV1): ByteArray {
        val keyId = value.keyId.encodeToByteArray()
        require(keyId.size in 8..128 && value.nonce.size() == 12) { "Invalid history associated data" }
        return try {
            ByteArrayOutputStream().use { output ->
                output.write(aadPrefix)
                output.write(value.sessionId.toByteArray())
                output.write(value.pcDeviceId.toByteArray())
                output.write(value.recipientDeviceId.toByteArray())
                output.write(ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value.keyEpoch).array())
                output.write(ByteBuffer.allocate(2).order(ByteOrder.BIG_ENDIAN).putShort(keyId.size.toShort()).array())
                output.write(keyId)
                output.write(ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value.startSeq).array())
                output.write(ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value.endSeq).array())
                output.write(value.nonce.toByteArray())
                output.toByteArray()
            }
        } finally {
            keyId.fill(0)
        }
    }
}

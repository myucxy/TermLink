package dev.termlink.crypto

import dev.termlink.protocol.realtime.v1.FrozenHistoryCiphertextV1
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters

object FrozenHistoryCrypto {
    const val CONTAINER_VERSION = 1
    const val PAGE_FORMAT_V1 = 1
    const val PAGE_FORMAT_V2 = 2
    const val MAX_PAGE_V1_BYTES = 16 * 1024 * 1024
    const val MAX_PAGE_V2_BYTES = 64 * 1024 * 1024
    const val OBJECT_OVERHEAD_BYTES = 4096

    private val keyPrefixLegacy = "TERMLINK-FROZEN-HISTORY-KEY-V1\u0000".encodeToByteArray()
    private val keyPrefixV1 = "TERMLINK-FROZEN-HISTORY-KEY-V1\u0000".encodeToByteArray()
    private val keyPrefixV2 = "TERMLINK-FROZEN-HISTORY-KEY-V2\u0000".encodeToByteArray()
    private val aadPrefixLegacy = "TERMLINK-FROZEN-HISTORY-OBJECT-V1\u0000".encodeToByteArray()
    private val aadPrefixV1 = "TERMLINK-FROZEN-HISTORY-OBJECT-V1\u0000".encodeToByteArray()
    private val aadPrefixV2 = "TERMLINK-FROZEN-HISTORY-OBJECT-V2\u0000".encodeToByteArray()
    const val STATE_CONTRACT_LEGACY = "terminal_state_2_21_v2"
    const val STATE_CONTRACT_V1 = "terminal_state_2_22_v1"
    const val STATE_CONTRACT_V2 = "terminal_state_2_22_v2"
    private val crcTable = IntArray(256) { index ->
        var crc = index
        repeat(8) { crc = (crc ushr 1) xor (0x82f63b78.toInt() and -(crc and 1)) }
        crc
    }

    fun keyId(recipientDeviceId: UUID, keyEpoch: Long, stateContract: String = STATE_CONTRACT_V1): String {
        require(recipientDeviceId != UUID(0, 0) && keyEpoch > 0) { "Frozen history key identity is invalid" }
        val normalized = normalizeStateContract(stateContract)
        val variant = if (normalized == STATE_CONTRACT_V2) "v2" else "v1"
        return "frozen-history-$variant:${recipientDeviceId.toString().replace("-", "")}:${keyEpoch.toString().padStart(8, '0')}"
    }

    /** The caller owns and must clear the returned plaintext array after decoding. */
    fun open(
        pairwiseKey: ByteArray,
        sealedObject: ByteArray,
        expectedSessionId: UUID,
        expectedPcDeviceId: UUID,
        expectedRecipientDeviceId: UUID,
        expectedKeyEpoch: Long,
        expectedToken: ByteArray,
        expectedCaptureId: ULong,
        expectedCaptureRevision: ULong,
        expectedSnapshotSeq: ULong,
        expectedRequestBeforeLineId: ULong,
        expectedMaxLines: Int,
        expectedMaxCells: Int,
        expectedMaxUtf8Bytes: Int,
        expectedPageFormatVersion: Int,
        expectedStateContract: String,
    ): ByteArray {
        validateIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch)
        validateRequest(expectedToken, expectedCaptureId, expectedCaptureRevision, expectedSnapshotSeq,
            expectedRequestBeforeLineId, expectedMaxLines, expectedMaxCells, expectedMaxUtf8Bytes, expectedPageFormatVersion)
        validateStateContract(expectedStateContract, expectedPageFormatVersion)
        require(sealedObject.size in 1..(pageLimit(expectedPageFormatVersion) + OBJECT_OVERHEAD_BYTES)) {
            "Frozen history sealed object size is invalid"
        }
        val value = FrozenHistoryCiphertextV1.parseFrom(sealedObject)
        require(
            value.containerVersion == CONTAINER_VERSION &&
                value.sessionId.toByteArray().contentEquals(SessionCrypto.uuidBytes(expectedSessionId)) &&
                value.pcDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(expectedPcDeviceId)) &&
                value.recipientDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(expectedRecipientDeviceId)) &&
                value.keyEpoch == expectedKeyEpoch && value.keyId == keyId(expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract) &&
                value.token.toByteArray().contentEquals(expectedToken) &&
                value.captureId.toULong() == expectedCaptureId && value.captureRevision.toULong() == expectedCaptureRevision &&
                value.snapshotSeq.toULong() == expectedSnapshotSeq &&
                value.requestBeforeLineId.toULong() == expectedRequestBeforeLineId &&
                value.maxLines == expectedMaxLines && value.maxCells == expectedMaxCells &&
                value.maxUtf8Bytes == expectedMaxUtf8Bytes && value.pageFormatVersion == expectedPageFormatVersion &&
                value.plaintextBytes in 96L..pageLimit(expectedPageFormatVersion).toLong() &&
                value.nonce.size() == 12 && value.ciphertext.size().toLong() == value.plaintextBytes + 16,
        ) { "Frozen history object metadata does not match the request" }
        val key = deriveKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedStateContract)
        val aad = associatedData(value, expectedStateContract)
        val nonce = value.nonce.toByteArray()
        val plaintext = ByteArray(value.plaintextBytes.toInt())
        try {
            Cipher.getInstance("AES/GCM/NoPadding").run {
                init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
                updateAAD(aad)
                val ciphertext = value.ciphertext.toByteArray()
                require(doFinal(ciphertext, 0, ciphertext.size, plaintext) == plaintext.size) {
                    "Frozen history plaintext length does not match authenticated metadata"
                }
            }
            val bounds = readPage(plaintext, expectedCaptureId, expectedCaptureRevision,
                expectedRequestBeforeLineId, expectedMaxLines, expectedMaxCells, expectedMaxUtf8Bytes,
                expectedPageFormatVersion)
            require(value.firstLineId.toULong() == bounds.first && value.lastLineId.toULong() == bounds.last &&
                value.hasMoreBefore == bounds.hasMore && value.nextBeforeLineId.toULong() == bounds.next) {
                "Frozen history page boundaries do not match its authenticated container"
            }
            return plaintext
        } catch (error: Throwable) {
            plaintext.fill(0)
            throw error
        } finally {
            key.fill(0)
            aad.fill(0)
            nonce.fill(0)
        }
    }

    internal fun associatedData(value: FrozenHistoryCiphertextV1, stateContract: String): ByteArray {
        validateStateContract(stateContract, value.pageFormatVersion)
        require(value.sessionId.size() == 16 && value.pcDeviceId.size() == 16 &&
            value.recipientDeviceId.size() == 16 && value.token.size() == 32 && value.nonce.size() == 12) {
            "Frozen history authenticated metadata is invalid"
        }
        val keyIdBytes = value.keyId.encodeToByteArray()
        val stateBytes = stateContract.encodeToByteArray()
        require(keyIdBytes.size in 8..128) { "Frozen history key ID is invalid" }
        return try {
            ByteArrayOutputStream().use { output ->
                output.write(aadPrefix(stateContract))
                output.write(intBytes(value.containerVersion))
                output.write(value.sessionId.toByteArray())
                output.write(value.pcDeviceId.toByteArray())
                output.write(value.recipientDeviceId.toByteArray())
                output.write(longBytes(value.keyEpoch))
                output.write(shortBytes(keyIdBytes.size))
                output.write(keyIdBytes)
                output.write(value.token.toByteArray())
                listOf(value.captureId, value.captureRevision, value.snapshotSeq, value.requestBeforeLineId).forEach {
                    output.write(longBytes(it))
                }
                listOf(value.maxLines, value.maxCells, value.maxUtf8Bytes, value.pageFormatVersion).forEach {
                    output.write(intBytes(it))
                }
                output.write(shortBytes(stateBytes.size))
                output.write(stateBytes)
                output.write(longBytes(value.plaintextBytes))
                output.write(longBytes(value.firstLineId))
                output.write(longBytes(value.lastLineId))
                output.write(if (value.hasMoreBefore) 1 else 0)
                output.write(longBytes(value.nextBeforeLineId))
                output.write(value.nonce.toByteArray())
                output.toByteArray()
            }
        } finally {
            keyIdBytes.fill(0)
            stateBytes.fill(0)
        }
    }

    private fun deriveKey(pairwiseKey: ByteArray, sessionId: UUID, recipientDeviceId: UUID, keyEpoch: Long,
        stateContract: String): ByteArray {
        val keyIdBytes = keyId(recipientDeviceId, keyEpoch, stateContract).encodeToByteArray()
        val info = ByteArrayOutputStream().use { output ->
            output.write(keyPrefix(stateContract))
            output.write(SessionCrypto.uuidBytes(recipientDeviceId))
            output.write(longBytes(keyEpoch))
            output.write(shortBytes(keyIdBytes.size))
            output.write(keyIdBytes)
            output.toByteArray()
        }
        return try {
            val generator = HKDFBytesGenerator(SHA256Digest())
            generator.init(HKDFParameters(pairwiseKey, SessionCrypto.uuidBytes(sessionId), info))
            ByteArray(32).also { generator.generateBytes(it, 0, it.size) }
        } finally {
            keyIdBytes.fill(0)
            info.fill(0)
        }
    }

    private data class Bounds(val first: ULong, val last: ULong, val hasMore: Boolean, val next: ULong)

    private fun readPage(page: ByteArray, captureId: ULong, revision: ULong, anchor: ULong,
        maxLines: Int, maxCells: Int, maxUtf8: Int, format: Int): Bounds {
        require(page.size in 96..pageLimit(format)) { "Frozen history page size is invalid" }
        val view = ByteBuffer.wrap(page).order(ByteOrder.LITTLE_ENDIAN)
        fun u32(offset: Int) = view.getInt(offset).toLong() and 0xffff_ffffL
        fun u64(offset: Int) = view.getLong(offset).toULong()
        val magic = "TLHPV$format\u0000\u0000".encodeToByteArray()
        val lines = u32(48)
        val flags = u32(68)
        require(page.copyOfRange(0, 8).contentEquals(magic) &&
            view.getShort(8).toInt() == format && view.getShort(10).toInt() == 96 &&
            u32(12) == page.size.toLong() && u64(16) == captureId && u64(24) == revision && u64(32) == anchor &&
            u32(72) == (format - 1).toLong() && (76..91).all { page[it] == 0.toByte() } &&
            lines <= maxLines && lines <= 128 && u32(52) <= u32(60) && u32(56) in 1..512 &&
            u32(60) <= maxCells && u32(64) <= maxUtf8 &&
            flags <= 3 && page.size >= 96 + 40 * lines) {
            "Frozen history TLHP page header is invalid or does not match the request"
        }
        var crc = -1
        page.forEachIndexed { index, byte ->
            if (index !in 92..95) crc = (crc ushr 8) xor crcTable[(crc xor byte.toInt()) and 255]
        }
        require((crc.inv().toLong() and 0xffff_ffffL) == u32(92)) { "Frozen history TLHP CRC is invalid" }
        val first = if (lines == 0L) 0uL else u64(96)
        val last = if (lines == 0L) 0uL else u64(96 + (lines.toInt() - 1) * 40)
        val next = u64(40)
        require(next == (if (lines == 0L) anchor else first) &&
            (lines != 0L || (flags == 0L && u32(52) == 0L && u32(60) == 0L && u32(64) == 0L))) {
            "Frozen history TLHP page boundaries are invalid"
        }
        return Bounds(first, last, flags and 1 != 0L, next)
    }

    private fun validateIdentity(key: ByteArray, session: UUID, pc: UUID, recipient: UUID, epoch: Long) {
        require(key.size == 32 && session != UUID(0, 0) && pc != UUID(0, 0) &&
            recipient != UUID(0, 0) && pc != recipient && epoch > 0) {
            "Frozen history identity or key is invalid"
        }
    }

    private fun validateRequest(token: ByteArray, captureId: ULong, revision: ULong, snapshotSeq: ULong,
        anchor: ULong, maxLines: Int, maxCells: Int, maxUtf8: Int, format: Int) {
        require(token.size == 32 && captureId in 1uL..Long.MAX_VALUE.toULong() &&
            snapshotSeq in 1uL until Long.MAX_VALUE.toULong() &&
            revision == snapshotSeq + 1uL && anchor != 0uL && maxLines in 1..128 &&
            maxCells in 1..65_536 && maxUtf8 in 1..4 * 1024 * 1024 &&
            format in PAGE_FORMAT_V1..PAGE_FORMAT_V2) { "Frozen history request metadata is invalid" }
    }

    private fun pageLimit(format: Int): Int = when (format) {
        PAGE_FORMAT_V1 -> MAX_PAGE_V1_BYTES
        PAGE_FORMAT_V2 -> MAX_PAGE_V2_BYTES
        else -> throw IllegalArgumentException("Frozen history page format is unsupported")
    }
    private fun validateStateContract(stateContract: String, format: Int) {
        require(format in PAGE_FORMAT_V1..PAGE_FORMAT_V2 &&
            if (format == PAGE_FORMAT_V1) stateContract == STATE_CONTRACT_LEGACY
            else stateContract == STATE_CONTRACT_V1 || stateContract == STATE_CONTRACT_V2) {
            "Frozen history state contract does not match its page format"
        }
    }
    private fun normalizeStateContract(value: String): String = when (value) {
        STATE_CONTRACT_LEGACY, STATE_CONTRACT_V1, STATE_CONTRACT_V2 -> value
        else -> error("Frozen history state contract is unsupported")
    }
    private fun keyPrefix(value: String): ByteArray = when (normalizeStateContract(value)) {
        STATE_CONTRACT_LEGACY -> keyPrefixLegacy
        STATE_CONTRACT_V1 -> keyPrefixV1
        else -> keyPrefixV2
    }
    private fun aadPrefix(value: String): ByteArray = when (normalizeStateContract(value)) {
        STATE_CONTRACT_LEGACY -> aadPrefixLegacy
        STATE_CONTRACT_V1 -> aadPrefixV1
        else -> aadPrefixV2
    }
    private fun shortBytes(value: Int) = ByteBuffer.allocate(2).order(ByteOrder.BIG_ENDIAN).putShort(value.toShort()).array()
    private fun intBytes(value: Int) = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(value).array()
    private fun longBytes(value: Long) = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value).array()
}

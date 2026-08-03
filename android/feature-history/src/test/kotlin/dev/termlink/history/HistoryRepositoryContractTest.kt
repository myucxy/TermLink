package dev.termlink.history

import dev.termlink.model.HistoryObject
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class HistoryRepositoryContractTest {
    @Test
    fun sha256IsStableAndUsesTheCiphertextBytes() {
        assertEquals(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            HistoryRepository.sha256("abc".encodeToByteArray()).toHex(),
        )
    }

    @Test
    fun encryptedObjectRejectsHashMismatchBeforeUse() {
        val ciphertext = byteArrayOf(0x11, 0x22, 0x33)
        val metadata = HistoryObject(
            id = UUID.randomUUID(),
            kind = HistoryObject.Kind.CHUNK,
            objectKey = "chunks/session/1",
            keyId = "key-1",
            ciphertextSha256 = ByteArray(HistoryRepository.SHA256_BYTES),
            sizeBytes = ciphertext.size.toLong(),
            startSequence = 1u,
            endSequence = 1u,
            snapshotSequence = null,
            formatName = null,
            formatVersion = null,
        )
        assertFailsWith<IllegalArgumentException> {
            EncryptedHistoryObject(metadata, ciphertext)
        }
    }

    @Test
    fun replayEventCopiesPayloadForTransientResults() {
        val source = byteArrayOf(1, 2, 3)
        val event = HistoryReplayEvent(1u, source)
        val copy = event.copyPayload()
        source[0] = 9
        assertContentEquals(byteArrayOf(1, 2, 3), copy.payload)
        assertEquals(event.sequence, copy.sequence)
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}

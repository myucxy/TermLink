package dev.termlink.crypto

import java.io.File
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFails

class DeltaCryptoTest {
    private val file = generateSequence(File(System.getProperty("user.dir") ?: ".").absoluteFile) { it.parentFile }
        .map { File(it, "protocol/test-vectors/state-delta-sealed-v1.json") }.first(File::isFile)
    private val vector = Json.parseToJsonElement(file.readText()).jsonObject
    private fun text(name: String) = vector.getValue(name).jsonPrimitive.content
    private fun bytes(name: String) = text(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    private fun open(sealed: ByteArray = bytes("sealed_object"),
        context: ByteArray = bytes("context_id"), baseHash: ByteArray = bytes("base_sha256"),
        targetSequence: ULong = text("target_next_sequence").toULong(),
        stateContract: String = text("state_contract"), minimumEpoch: ULong = 10uL) = DeltaCrypto.open(
        bytes("pairwise_key"), sealed, UUID.fromString(text("session_id")),
        UUID.fromString(text("pc_device_id")), UUID.fromString(text("recipient_device_id")),
        text("key_epoch").toLong(), stateContract, context,
        text("profile").toInt(), text("base_next_sequence").toULong(), targetSequence,
        text("base_snapshot_bytes").toULong(), text("target_snapshot_bytes").toULong(),
        baseHash, bytes("target_sha256"), minimumEpoch,
    )

    @Test fun opensWindowsVectorAndRejectsTampering() {
        assertEquals("termlink.state-delta-sealed-v1", text("format"))
        assertEquals(DeltaCrypto.FORMAT_NAME, text("format_name"))
        assertEquals(DeltaCrypto.FORMAT_VERSION.toString(), text("format_version"))
        assertEquals(text("key_id"), DeltaCrypto.keyId(UUID.fromString(text("recipient_device_id")), 7))
        assertContentEquals(bytes("context_id"), DeltaCrypto.deriveContextId(
            UUID.fromString(text("session_id")), UUID.fromString(text("pc_device_id")), text("state_contract")))
        val sealed = bytes("sealed_object")
        assertContentEquals(bytes("associated_data"), DeltaCrypto.associatedData(sealed.copyOfRange(0, DeltaCrypto.HEADER_BYTES)))
        val opened = open()
        try {
            assertContentEquals(bytes("native_delta"), opened.delta)
            assertEquals(17uL, opened.targetObservedLocalInputEpoch)
            assertEquals(1uL, opened.targetInitialTranscriptBeforeLineId)
        } finally { opened.delta.fill(0) }
        assertFails { open(context = ByteArray(32)) }
        assertFails { open(context = ByteArray(32) { 1 }) }
        assertFails { open(stateContract = "terminal_state_2_21_v2") }
        assertFails { open(baseHash = ByteArray(32)) }
        assertFails { open(targetSequence = 5204uL) }
        assertFails { open(minimumEpoch = 18uL) }
        listOf(0, 8, 10, 12, 28, 44, 60, 68, 133, 158, 190, 194, 202, 210,
            218, 226, 258, 290, 294, 306, 314, 322, DeltaCrypto.HEADER_BYTES, sealed.lastIndex).forEach { index ->
            val changed = sealed.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
            assertFails { open(changed) }
        }
    }

    @Test fun rejectsOversizedObjectBeforeParsing() {
        assertEquals(8 * 1024 * 1024, DeltaCrypto.MAXIMUM_OBJECT_BYTES)
        assertFails { open(ByteArray(DeltaCrypto.MAXIMUM_OBJECT_BYTES + 1)) }
    }
}

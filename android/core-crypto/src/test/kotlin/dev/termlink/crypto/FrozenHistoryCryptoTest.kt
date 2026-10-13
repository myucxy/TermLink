package dev.termlink.crypto

import com.google.protobuf.ByteString
import dev.termlink.protocol.realtime.v1.FrozenHistoryCiphertextV1
import java.io.File
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFails

class FrozenHistoryCryptoTest {
    private val file = generateSequence(File(System.getProperty("user.dir") ?: ".").absoluteFile) { it.parentFile }
        .map { File(it, "protocol/test-vectors/frozen-history-v1.json") }.first(File::isFile)
    private val vector = Json.parseToJsonElement(file.readText()).jsonObject
    private fun text(name: String) = vector.getValue(name).jsonPrimitive.content
    private fun bytes(name: String) = text(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private val session = UUID.fromString(text("session_id"))
    private val pc = UUID.fromString(text("pc_device_id"))
    private val recipient = UUID.fromString(text("recipient_device_id"))

    private fun open(sealed: ByteArray = bytes("sealed_object"), token: ByteArray = bytes("token"),
        stateContract: String = text("state_contract")) =
        FrozenHistoryCrypto.open(bytes("pairwise_key"), sealed, session, pc, recipient,
            text("key_epoch").toLong(), token, text("capture_id").toULong(),
            text("capture_revision").toULong(), text("snapshot_seq").toULong(),
            text("request_before_line_id").toULong(), text("max_lines").toInt(),
            text("max_cells").toInt(), text("max_utf8_bytes").toInt(),
            text("page_format_version").toInt(), stateContract)

    @Test fun opensWindowsVectorAndRejectsTampering() {
        assertEquals("termlink.frozen-history-v1", text("format"))
        assertEquals(FrozenHistoryCrypto.keyId(recipient, 7),
            FrozenHistoryCiphertextV1.parseFrom(bytes("sealed_object")).keyId)
        val page = open()
        try { assertContentEquals(bytes("page"), page) } finally { page.fill(0) }
        val container = FrozenHistoryCiphertextV1.parseFrom(bytes("sealed_object"))
        assertContentEquals(bytes("associated_data"), FrozenHistoryCrypto.associatedData(container, text("state_contract")))
        assertFails { open(token = ByteArray(32)) }
        assertFails { open(stateContract = "terminal_state_2_22_v1") }
        listOf<(FrozenHistoryCiphertextV1.Builder) -> Unit>(
            { it.captureId = 48 },
            { it.captureRevision = 5202 },
            { it.snapshotSeq = 5201 },
            { it.requestBeforeLineId = 7 },
            { it.maxLines = 127 },
            { it.maxCells = 65535 },
            { it.maxUtf8Bytes = 100 },
            { it.firstLineId = 1 },
            { it.lastLineId = 1 },
            { it.hasMoreBefore = true },
            { it.nextBeforeLineId = 7 },
            { it.plaintextBytes = 95 },
            { it.ciphertext = ByteString.copyFrom(it.ciphertext.toByteArray().also { bytes -> bytes[0] = (bytes[0].toInt() xor 1).toByte() }) },
        ).forEach { mutate ->
            val changed = container.toBuilder()
            mutate(changed)
            assertFails { open(changed.build().toByteArray()) }
        }
    }

    @Test fun rejectsOversizedObjectBeforeParsing() {
        assertFails { open(ByteArray(FrozenHistoryCrypto.MAX_PAGE_V1_BYTES + FrozenHistoryCrypto.OBJECT_OVERHEAD_BYTES + 1)) }
    }
}

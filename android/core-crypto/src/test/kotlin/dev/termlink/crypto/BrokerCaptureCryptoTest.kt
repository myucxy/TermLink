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

class BrokerCaptureCryptoTest {
    private val file = generateSequence(File(System.getProperty("user.dir") ?: ".").absoluteFile) { it.parentFile }
        .map { File(it, "protocol/test-vectors/broker-capture-sealed-v1.json") }.first(File::isFile)
    private val vector = Json.parseToJsonElement(file.readText()).jsonObject
    private fun text(name: String) = vector.getValue(name).jsonPrimitive.content
    private fun bytes(name: String) = text(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun uuid(name: String) = UUID.fromString(text(name))

    private fun open(sealed: ByteArray = bytes("sealed_object"),
        session: UUID = uuid("session_id"), objectId: UUID = uuid("object_id"),
        sequence: ULong = text("source_sequence").toULong(),
        length: Int = text("canonical_length").toInt(),
        digest: ByteArray = bytes("canonical_sha256")) = BrokerCaptureCrypto.open(
        bytes("pairwise_key"), sealed, session, uuid("pc_device_id"),
        uuid("recipient_device_id"), objectId, sequence, text("key_epoch").toLong(), length, digest)

    @Test fun opensWindowsVectorAndRejectsTampering() {
        assertEquals("termlink.broker-capture-sealed-v1", text("format"))
        assertEquals(text("key_id"), BrokerCaptureCrypto.keyId(uuid("recipient_device_id"), 7))
        val sealed = bytes("sealed_object")
        assertContentEquals(bytes("associated_data"), BrokerCaptureCrypto.associatedData(
            sealed.copyOfRange(0, BrokerCaptureCrypto.HEADER_BYTES)))
        val opened = open()
        try { assertContentEquals(bytes("canonical"), opened) } finally { opened.fill(0) }
        assertContentEquals(sealed, BrokerCaptureCrypto.seal(bytes("pairwise_key"),
            uuid("session_id"), uuid("pc_device_id"), uuid("recipient_device_id"),
            uuid("object_id"), text("source_sequence").toULong(), text("key_epoch").toLong(),
            bytes("canonical"), bytes("nonce")))
        assertFails { open(session = UUID.randomUUID()) }
        assertFails { open(objectId = UUID.randomUUID()) }
        assertFails { open(sequence = 10uL) }
        assertFails { open(length = 137) }
        assertFails { open(digest = ByteArray(32)) }
        listOf(0, 8, 10, 12, 28, 44, 60, 76, 84, 92, 96, 128, 140, 162, 172,
            BrokerCaptureCrypto.HEADER_BYTES, sealed.lastIndex).forEach { index ->
            val changed = sealed.copyOf().also { it[index] = (it[index].toInt() xor 1).toByte() }
            assertFails { open(changed) }
        }
    }

    @Test fun rejectsOversizedAndMalformedCanonical() {
        assertFails { open(ByteArray(BrokerCaptureCrypto.MAXIMUM_OBJECT_BYTES + 1)) }
        assertFails { BrokerCaptureCrypto.seal(bytes("pairwise_key"), uuid("session_id"),
            uuid("pc_device_id"), uuid("recipient_device_id"), uuid("object_id"), 9uL,
            7, ByteArray(BrokerCaptureCrypto.MAXIMUM_CANONICAL_BYTES + 1)) }
        val wrong = bytes("canonical").also { it[16] = (it[16].toInt() xor 1).toByte() }
        assertFails { BrokerCaptureCrypto.seal(bytes("pairwise_key"), uuid("session_id"),
            uuid("pc_device_id"), uuid("recipient_device_id"), uuid("object_id"), 9uL,
            7, wrong) }
    }
}

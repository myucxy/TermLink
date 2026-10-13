package dev.termlink.crypto

import com.google.protobuf.ByteString
import dev.termlink.protocol.realtime.v1.TerminalCoreSnapshotCiphertextV2
import java.io.File
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.bouncycastle.crypto.InvalidCipherTextException
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class TerminalCoreGraphicsSnapshotCryptoTest {
    private val crypto = TerminalSnapshotCrypto
    private val pc = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
    private val recipient = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
    private val session = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
    private val key = hex("b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773")
    private val keyId = crypto.terminalCoreGraphicsSnapshotKeyId(recipient, 7)

    @Test
    fun opensAndReproducesSharedFixedVector() {
        val file = generateSequence(File(System.getProperty("user.dir") ?: ".").absoluteFile) { it.parentFile }
            .map { File(it, "protocol/test-vectors/terminal-core-graphics-snapshot-v1.json") }
            .first(File::isFile)
        val vector = Json.parseToJsonElement(file.readText()).jsonObject
        fun text(name: String) = vector.getValue(name).jsonPrimitive.content
        fun bytes(name: String) = hex(text(name))
        assertEquals("termlink.terminal-core-graphics-snapshot-v1", text("format"))
        assertEquals("1", text("format_version"))
        assertEquals(pc.toString(), text("pc_device_id"))
        assertEquals(recipient.toString(), text("android_device_id"))
        assertEquals(session.toString(), text("session_id"))
        assertEquals("7", text("key_epoch"))
        assertEquals("5200", text("snapshot_sequence"))
        assertEquals("132", text("columns"))
        assertEquals("43", text("rows"))
        assertEquals("4097", text("initial_transcript_before_line_id"))
        assertEquals("11", text("observed_local_input_epoch"))
        assertEquals(keyId, text("key_id"))
        assertContentEquals(key, bytes("pairwise_key"))
        assertContentEquals(bytes("key"), crypto.deriveTerminalCoreGraphicsSnapshotKey(key, session, recipient, 7, keyId))
        val sealed = seal(bytes("checkpoint"), bytes("nonce"))
        assertContentEquals(bytes("sealed_object"), sealed)
        val container = TerminalCoreSnapshotCiphertextV2.parseFrom(sealed)
        assertContentEquals(bytes("associated_data"), crypto.terminalCoreGraphicsSnapshotAssociatedData(container))
        val opened = open(bytes("sealed_object"))
        assertContentEquals(bytes("checkpoint"), opened.checkpoint)
        assertEquals(4097uL, opened.initialTranscriptBeforeLineId)
        assertEquals(11uL, opened.observedLocalInputEpoch)
        assertEquals(3, opened.containerVersion)
        assertEquals("TERMLINK_TERMINAL_CORE_GRAPHICS_SNAPSHOT", opened.formatName)
    }

    @Test
    fun rejectsLegacyAndGraphicsDowngrades() {
        val sealed = seal(byteArrayOf(1, 2, 3))
        assertFailsWith<IllegalArgumentException> { openLegacy(sealed) }
        val legacy = crypto.sealTerminalCoreSnapshot(key, session, pc, recipient, 7, 5200uL,
            132, 43, 4097uL, 11uL, byteArrayOf(1, 2, 3), ByteArray(12))
        assertFailsWith<IllegalArgumentException> { open(legacy) }
        val downgraded = TerminalCoreSnapshotCiphertextV2.parseFrom(sealed).toBuilder()
            .setContainerVersion(crypto.TERMINAL_CORE_SNAPSHOT_CONTAINER_VERSION)
            .setFormatName(crypto.TERMINAL_CORE_SNAPSHOT_FORMAT_NAME)
            .setFormatVersion(crypto.TERMINAL_CORE_SNAPSHOT_FORMAT_VERSION)
            .setKeyId(crypto.terminalCoreSnapshotKeyId(recipient, 7)).build()
        assertFailsWith<InvalidCipherTextException> { openLegacy(downgraded.toByteArray()) }
    }

    @Test
    fun rejectsFormatMetadataAndAadTampering() {
        val container = TerminalCoreSnapshotCiphertextV2.parseFrom(seal(byteArrayOf(1, 2, 3)))
        val invalid = listOf<(TerminalCoreSnapshotCiphertextV2.Builder) -> Unit>(
            { it.setContainerVersion(2) },
            { it.setFormatVersion(2) },
            { it.setFormatName(crypto.TERMINAL_CORE_SNAPSHOT_FORMAT_NAME) },
            { it.setCompressionValue(2) },
            { it.setUncompressedBytes(0) },
            { it.setUncompressedBytes(crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_PLAINTEXT_BYTES.toLong() + 1) },
            { it.setUncompressedBytes(4) },
            { it.setNonce(ByteString.copyFrom(ByteArray(11))) },
        )
        invalid.forEach { mutate ->
            val builder = container.toBuilder()
            mutate(builder)
            assertFailsWith<IllegalArgumentException> { open(builder.build().toByteArray()) }
        }
        assertFailsWith<InvalidCipherTextException> {
            open(container.toBuilder().setObservedLocalInputEpoch(12).build().toByteArray())
        }
        val maxMetadata = container.toBuilder()
            .setUncompressedBytes(crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_PLAINTEXT_BYTES.toLong()).build()
        crypto.terminalCoreGraphicsSnapshotAssociatedData(maxMetadata)
        assertFailsWith<IllegalArgumentException> { crypto.terminalCoreSnapshotAssociatedData(maxMetadata) }
    }

    @Test
    fun rejectsOversizedObjectBeforeParsing() {
        assertEquals(64 * 1024 * 1024, crypto.MAX_TERMINAL_CORE_SNAPSHOT_OBJECT_BYTES)
        assertEquals(64 * 1024 * 1024 - 4096, crypto.MAX_TERMINAL_CORE_SNAPSHOT_PLAINTEXT_BYTES)
        assertEquals(128 * 1024 * 1024 + 4096, crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_OBJECT_BYTES)
        val oversized = ByteArray(crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_OBJECT_BYTES + 1)
        assertEquals("Sealed terminal-core snapshot size is invalid",
            assertFailsWith<IllegalArgumentException> { open(oversized) }.message)
    }

    @Test
    fun rejectsOversizedPlaintextBeforeCopyingOrEncrypting() {
        val oversized = ByteArray(crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_PLAINTEXT_BYTES + 1)
        assertEquals("Terminal-core snapshot metadata is invalid or unsupported",
            assertFailsWith<IllegalArgumentException> { seal(oversized, ByteArray(0)) }.message)
    }

    @Test
    fun admitsExactPlaintextLimitBeforeNonceValidation() {
        assertEquals(128 * 1024 * 1024, crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_PLAINTEXT_BYTES)
        val boundary = ByteArray(crypto.MAX_TERMINAL_CORE_GRAPHICS_SNAPSHOT_PLAINTEXT_BYTES)
        assertEquals("Terminal-core snapshot nonce must be 12 bytes",
            assertFailsWith<IllegalArgumentException> { seal(boundary, ByteArray(0)) }.message)
    }

    private fun seal(checkpoint: ByteArray, nonce: ByteArray = ByteArray(12)) =
        crypto.sealTerminalCoreGraphicsSnapshot(key, session, pc, recipient, 7, 5200uL,
            132, 43, 4097uL, 11uL, checkpoint, nonce)

    private fun open(sealed: ByteArray) = crypto.openTerminalCoreGraphicsSnapshot(
        key, sealed, session, pc, recipient, 7, keyId, 5200uL, 132, 43)

    private fun openLegacy(sealed: ByteArray) = crypto.openTerminalCoreSnapshot(
        key, sealed, session, pc, recipient, 7, crypto.terminalCoreSnapshotKeyId(recipient, 7), 5200uL, 132, 43)

    private fun hex(value: String) = ByteArray(value.length / 2) {
        value.substring(it * 2, it * 2 + 2).toInt(16).toByte()
    }
}

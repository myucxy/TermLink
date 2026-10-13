package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.io.File
import java.util.UUID
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class TerminalBrokerOutputReferenceV1Test {
    @Test
    fun externalReferenceRoundTripsRfcUuidAndDetachesBytes() {
        val output = "synthetic broker output".encodeToByteArray()
        val hash = ByteArray(32) { it.toByte() }
        val id = UUID.fromString("7a2c772e-e768-401f-a323-2f4bcd309c2b")
        TerminalBrokerOutputReferenceV1.external(2, output, id, 244, hash).use { reference ->
            output.fill(0)
            hash.fill(0)
            val wire = reference.encode()
            assertContentEquals(sharedReferenceVector(), wire)
            assertContentEquals("7a2c772ee768401fa3232f4bcd309c2b".hexToBytes(), wire.copyOfRange(24, 40))
            TerminalBrokerOutputReferenceV1.decode(wire).use { decoded ->
                assertFalse(decoded.isInline)
                assertEquals(id, decoded.objectId)
                assertEquals(2L, decoded.sourceSequence)
                assertEquals(244, decoded.captureBytes)
                assertContentEquals(wire, decoded.encode())
                wire.fill(0)
                assertContentEquals("synthetic broker output".encodeToByteArray(), decoded.copyOutput())
            }
        }
    }

    @Test
    fun inlineFailuresValidateAllRecordsAndDigest() {
        val output = byteArrayOf(65, 66)
        val failures = inlineFailure(2)
        val reference = TerminalBrokerOutputReferenceV1.inlineFailures(2, output, failures)
        val wire = reference.encode()
        failures.fill(0)
        TerminalBrokerOutputReferenceV1.decode(wire).use { decoded ->
            assertTrue(decoded.isInline)
            assertContentEquals(wire, decoded.encode())
            assertEquals(136, decoded.copyInlineCaptures().size)
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalBrokerOutputReferenceV1.decode(wire.copyOf().also { it[48] = (it[48].toInt() xor 1).toByte() })
        }
        assertFailsWith<IllegalArgumentException> { TerminalBrokerOutputReferenceV1.decode(wire.copyOf(wire.size - 1)) }
        reference.close()
        reference.close()
        assertFailsWith<IllegalStateException> { reference.copyOutput() }
    }

    private fun inlineFailure(sequence: Long): ByteArray = ByteBuffer.allocate(136)
        .order(ByteOrder.LITTLE_ENDIAN).apply {
            put("TLCAPV1\u0000".encodeToByteArray())
            putInt(32); putInt(1); putLong(sequence); putInt(136); putInt(0)
            putInt(1); putInt(96); putInt(1); putInt('f'.code)
            putLong(sequence); putInt(1); putInt(0)
            position(128); putInt(4); putInt(0)
        }.array()

    private fun String.hexToBytes(): ByteArray = chunked(2).map { it.toInt(16).toByte() }.toByteArray()

    private fun sharedReferenceVector(): ByteArray {
        val file = listOf("protocol/test-vectors/broker-output-reference-v1.json",
            "../protocol/test-vectors/broker-output-reference-v1.json",
            "../../protocol/test-vectors/broker-output-reference-v1.json")
            .map(::File).firstOrNull { it.isFile } ?: error("Shared broker reference vector is missing")
        val hex = Regex("\"encoded_hex\"\\s*:\\s*\"([0-9a-f]+)\"")
            .find(file.readText())?.groupValues?.get(1) ?: error("Shared broker reference hex is missing")
        return hex.hexToBytes()
    }
}

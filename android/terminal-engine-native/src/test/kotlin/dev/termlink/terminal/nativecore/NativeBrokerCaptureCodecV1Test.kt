package dev.termlink.terminal.nativecore

import java.io.File
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeBrokerCaptureCodecV1Test {
    @Test
    fun sharedWindowsVectorDecodesAndEncodesExactly() {
        val encoded = sharedVector()
        assertEquals(244, encoded.size)
        NativeBrokerCaptureCodecV1.decode(encoded).use { group ->
            assertEquals(2L, group.sourceSequence)
            assertEquals(2, group.captures.size)
            assertEquals(NativeBrokerOutcomeV1.BYTES, group.captures[0].outcome)
            assertContentEquals(byteArrayOf(1, 2, 3, 4), group.captures[0].copyBytes())
            assertEquals(NativeBrokerOutcomeV1.UNSUPPORTED, group.captures[1].outcome)
            assertTrue(group.captures[1].copyBytes().isEmpty())
            assertContentEquals(encoded, NativeBrokerCaptureCodecV1.encode(group.sourceSequence, group.captures))
            encoded.fill(0)
            assertContentEquals(byteArrayOf(1, 2, 3, 4), group.captures[0].copyBytes())
        }
    }

    @Test
    fun rejectsMalformedHeaderRecordsAndTrailingBytesBeforeAllocatingCaptures() {
        val valid = sharedVector()
        val malformed = listOf(
            valid.copyOf().also { it[0] = 0 },
            valid.copyOf().also { it[8] = 31 },
            valid.copyOf().also { it[12] = 0 },
            valid.copyOf().also { it[16] = 0 },
            valid.copyOf().also { it[24] = 0 },
            valid.copyOf().also { it[28] = 1 },
            valid.copyOf().also { it[56] = 0 }, // first identity offset must be positive
            valid.copyOf().also { it[60] = 1 }, // first ordinal must be zero
            valid.copyOf().also { it[128] = 5 }, // unknown outcome
            valid.copyOf().also { it[132] = 0xff.toByte() }, // record length beyond bound
            valid.copyOf(valid.size - 1),
        )
        malformed.forEachIndexed { index, bytes ->
            assertFailsWith<IllegalArgumentException>("malformed vector $index") {
                NativeBrokerCaptureCodecV1.decode(bytes)
            }
        }
        val trailing = valid.copyOf(valid.size + 1).also { it[24] = it.size.toByte() }
        assertFailsWith<IllegalArgumentException> { NativeBrokerCaptureCodecV1.decode(trailing) }
    }

    @Test
    fun encoderRejectsNonCanonicalGroupWithoutChangingInputs() {
        NativeBrokerCaptureCodecV1.decode(sharedVector()).use { group ->
            assertFailsWith<IllegalArgumentException> { NativeBrokerCaptureCodecV1.encode(0, group.captures) }
            assertFailsWith<IllegalArgumentException> { NativeBrokerCaptureCodecV1.encode(3, group.captures) }
            assertFailsWith<IllegalArgumentException> { NativeBrokerCaptureCodecV1.encode(2, emptyList()) }
            assertContentEquals(byteArrayOf(1, 2, 3, 4), group.captures[0].copyBytes())
        }
    }

    private fun sharedVector(): ByteArray {
        val candidate = listOf("protocol/test-vectors/broker-captures-v1.json",
            "../protocol/test-vectors/broker-captures-v1.json",
            "../../protocol/test-vectors/broker-captures-v1.json")
            .map(::File).firstOrNull { it.isFile } ?: error("Shared broker capture vector is missing")
        val encoded = Regex("\"encoded_hex\"\\s*:\\s*\"([0-9a-f]+)\"")
            .find(candidate.readText())?.groupValues?.get(1) ?: error("Shared vector has no encoded_hex")
        return encoded.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }
}

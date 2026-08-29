package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalHostEffectKindV1
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32C
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class HostEffectFrameV1DecoderTest {
    @Test
    fun decodesEmptyCanonicalFrame() {
        val frame = HostEffectFrameV1Decoder.decode(buildFrame(emptyList()))

        assertEquals(1, frame.formatVersion)
        assertTrue(frame.effects.isEmpty())
        assertEquals(0L, frame.payloadByteCount)
    }

    @Test
    fun decodesOrderedOwnedPtyRepliesWithoutAnAuthorityExecutionSurface() {
        val encoded = buildFrame(
            listOf(
                WireEffect(7, 4, 0, reply("\u001b[?1;2c")),
                WireEffect(7, 5, 0, reply("\u001b[>0;0;0c")),
                WireEffect(7, 9, 0, reply("\u001b[512;1R")),
                WireEffect(8, 1, 0, reply("\u001b[?1;512R")),
            ),
        )
        val frame = HostEffectFrameV1Decoder.decode(encoded)
        encoded.fill(0)

        assertEquals(4, frame.effects.size)
        assertEquals(33L, frame.payloadByteCount)
        assertTrue(frame.effects.all { it.kind == TerminalHostEffectKindV1.PTY_REPLY })
        assertEquals(listOf(7L, 7L, 7L, 8L), frame.effects.map { it.sourceSequence })
        assertEquals(listOf(4, 5, 9, 1), frame.effects.map { it.sourceOffsetEnd })
        assertEquals(listOf(0L, 0L, 0L, 0L), frame.effects.map { it.ordinal })
        assertContentEquals(reply("\u001b[?1;2c"), frame.effects[0].copyPayload())
        frame.effects[0].copyPayload()[0] = 0
        assertContentEquals(reply("\u001b[?1;2c"), frame.effects[0].copyPayload())
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.effects as MutableList<Any?>).clear()
        }
    }

    @Test
    fun rejectsMagicVersionFeatureAndEveryReservedField() {
        val baseline = standardFrame()
        val mutations = listOf(
            baseline.mutateAndRefresh(0, 'X'.code),
            baseline.putU16AndRefresh(8, 2),
            baseline.putU16AndRefresh(10, 1),
            baseline.putU16AndRefresh(12, 63),
            baseline.putU16AndRefresh(14, 39),
            baseline.putU64AndRefresh(40, 0),
            baseline.putU64AndRefresh(40, 3),
            baseline.putU64AndRefresh(48, 1),
            baseline.putU32AndRefresh(recordOffset(0) + 4, 1),
            baseline.putU32AndRefresh(recordOffset(0) + 36, 1),
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun rejectsHeaderRecordAndPayloadCrcMutations() {
        val baseline = standardFrame()
        val mutations = listOf(
            baseline.copyOf().also { it[16] = (it[16] + 1).toByte() },
            baseline.copyOf().also { it[recordOffset(0)] = (it[recordOffset(0)] + 1).toByte() },
            baseline.copyOf().also { it[payloadOffset(it)] = (it[payloadOffset(it)] + 1).toByte() },
            baseline.copyOf().also { it[56] = (it[56] + 1).toByte() },
            baseline.copyOf().also { it[60] = (it[60] + 1).toByte() },
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun rejectsNonCanonicalEnvelopeOffsetsLengthsOverflowAndTrailingData() {
        val baseline = standardFrame()
        val mutations = listOf(
            baseline.putU32AndRefresh(20, baseline.size - 1),
            baseline.putU32AndRefresh(20, 0xffff_ffffL),
            baseline.putU64AndRefresh(24, 63),
            baseline.putU64AndRefresh(24, Long.MIN_VALUE),
            baseline.putU64AndRefresh(32, payloadOffset(baseline) + 1L),
            baseline.putU64AndRefresh(32, Long.MIN_VALUE),
            baseline.putU32AndRefresh(16, 0xffff_ffffL),
            baseline.putU64AndRefresh(recordOffset(0) + 24, Long.MIN_VALUE),
            baseline.putU32AndRefresh(recordOffset(0) + 32, 0xffff_ffffL),
        )
        mutations.forEach(::assertRejected)

        val trailing = baseline.copyOf(baseline.size + 1).also {
            it[it.lastIndex] = 1
            putU32(it, 20, it.size)
            refreshCrc(it)
        }
        assertRejected(trailing)

        val overLimit = ByteArray(MAX_FRAME_BYTES + 1)
        assertRejected(overLimit)
    }

    @Test
    fun rejectsUnknownKindOffsetBoundsAndEmptyPayload() {
        val baseline = standardFrame()
        val mutations = listOf(
            baseline.putU32AndRefresh(recordOffset(0), 0),
            baseline.putU32AndRefresh(recordOffset(0), 2),
            baseline.putU32AndRefresh(recordOffset(0) + 16, 0),
            baseline.putU32AndRefresh(recordOffset(0) + 16, 1024 * 1024 + 1),
            baseline.putU32AndRefresh(recordOffset(0) + 32, 0),
            baseline.putU64AndRefresh(recordOffset(0) + 8, Long.MIN_VALUE),
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun acceptsExactlyTheSixCanonicalPtyReplyForms() {
        val replies = listOf(
            "\u001b[?1;2c",
            "\u001b[>0;0;0c",
            "\u001b[0n",
            "\u001b[?0n",
            "\u001b[1;512R",
            "\u001b[?512;1R",
        )
        val frame = HostEffectFrameV1Decoder.decode(
            buildFrame(
                replies.mapIndexed { index, value ->
                    WireEffect(10, index + 1, 0, reply(value))
                },
            ),
        )

        assertEquals(replies, frame.effects.map { it.copyPayload().toString(Charsets.US_ASCII) })
    }

    @Test
    fun rejectsNonCanonicalPtyRepliesLeadingZeroCoordinatesAndOutOfRangeCoordinates() {
        val invalidReplies = listOf(
            "x",
            "\u001b[?1;2C",
            "\u001b[>0;0c",
            "\u001b[00n",
            "\u001b[?00n",
            "\u001b[0;1R",
            "\u001b[1;0R",
            "\u001b[01;1R",
            "\u001b[1;01R",
            "\u001b[513;1R",
            "\u001b[1;513R",
            "\u001b[?513;1R",
            "\u001b[?1;513R",
            "\u001b[1;1r",
            "\u001b[1;;1R",
            "\u001b[1;1R!",
            "\u001b[1234;1R",
        )

        invalidReplies.forEach { payload ->
            assertRejected(buildFrame(listOf(WireEffect(1, 1, 0, reply(payload)))))
        }
    }

    @Test
    fun rejectsPayloadHolesOverlapsAndHeaderToRecordPadding() {
        val baseline = standardFrame()
        val firstPayloadOffsetField = recordOffset(0) + 24
        val secondPayloadOffsetField = recordOffset(1) + 24
        val mutations = listOf(
            baseline.putU64AndRefresh(firstPayloadOffsetField, payloadOffset(baseline) + 1L),
            baseline.putU64AndRefresh(firstPayloadOffsetField, payloadOffset(baseline) - 1L),
            baseline.putU64AndRefresh(
                secondPayloadOffsetField,
                getU64(baseline, secondPayloadOffsetField) + 1L,
            ),
            baseline.putU64AndRefresh(
                secondPayloadOffsetField,
                getU64(baseline, secondPayloadOffsetField) - 1L,
            ),
            baseline.putU64AndRefresh(32, payloadOffset(baseline) + 8L),
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun rejectsOrderingRegressionsDuplicateTuplesAndNonzeroOrdinals() {
        val baseline = standardFrame()
        val first = recordOffset(0)
        val second = recordOffset(1)
        val third = recordOffset(2)
        val fourth = recordOffset(3)
        val mutations = listOf(
            baseline.putU32AndRefresh(first + 20, 1),
            baseline.putU32AndRefresh(second + 20, 1),
            baseline.putU32AndRefresh(second + 16, 4),
            baseline.putU32AndRefresh(third + 16, 3),
            baseline.putU32AndRefresh(third + 20, 2),
            baseline.putU64AndRefresh(fourth + 8, 6),
            baseline.putU64AndRefresh(fourth + 8, 7).putU32AndRefresh(fourth + 16, 9),
            baseline.putU32AndRefresh(fourth + 20, 1),
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun rejectsEveryTruncatedPrefixDeterministically() {
        val frame = standardFrame()
        for (length in 0 until frame.size) {
            assertRejected(frame.copyOf(length))
        }
    }

    private fun assertRejected(bytes: ByteArray) {
        assertFailsWith<HostEffectFrameV1DecodeException> {
            HostEffectFrameV1Decoder.decode(bytes)
        }
    }

    private fun standardFrame(): ByteArray = buildFrame(
        listOf(
            WireEffect(7, 4, 0, reply("\u001b[?1;2c")),
            WireEffect(7, 5, 0, reply("\u001b[>0;0;0c")),
            WireEffect(7, 9, 0, reply("\u001b[0n")),
            WireEffect(8, 1, 0, reply("\u001b[?512;512R")),
        ),
    )

    private data class WireEffect(
        val sequence: Long,
        val sourceOffsetEnd: Int,
        val ordinal: Long,
        val payload: ByteArray,
    )

    private companion object {
        const val HEADER_SIZE = 64
        const val RECORD_SIZE = 40
        const val MAX_FRAME_BYTES = 65_710_862
        val MAGIC = byteArrayOf(
            'T'.code.toByte(), 'L'.code.toByte(), 'H'.code.toByte(), 'E'.code.toByte(),
            'F'.code.toByte(), '1'.code.toByte(), 0, 0,
        )

        fun reply(value: String): ByteArray = value.toByteArray(Charsets.US_ASCII)

        fun buildFrame(effects: List<WireEffect>): ByteArray {
            val payloadOffset = HEADER_SIZE + effects.size * RECORD_SIZE
            val totalLength = payloadOffset + effects.sumOf { it.payload.size }
            val frame = ByteArray(totalLength)
            MAGIC.copyInto(frame)
            putU16(frame, 8, 1)
            putU16(frame, 10, 0)
            putU16(frame, 12, HEADER_SIZE)
            putU16(frame, 14, RECORD_SIZE)
            putU32(frame, 16, effects.size)
            putU32(frame, 20, totalLength)
            putU64(frame, 24, HEADER_SIZE.toLong())
            putU64(frame, 32, payloadOffset.toLong())
            putU64(frame, 40, 1)
            var nextPayloadOffset = payloadOffset
            effects.forEachIndexed { index, effect ->
                val record = recordOffset(index)
                putU32(frame, record, 1)
                putU32(frame, record + 4, 0)
                putU64(frame, record + 8, effect.sequence)
                putU32(frame, record + 16, effect.sourceOffsetEnd)
                putU32(frame, record + 20, effect.ordinal)
                putU64(frame, record + 24, nextPayloadOffset.toLong())
                putU32(frame, record + 32, effect.payload.size)
                putU32(frame, record + 36, 0)
                effect.payload.copyInto(frame, nextPayloadOffset)
                nextPayloadOffset += effect.payload.size
            }
            return refreshCrc(frame)
        }

        fun recordOffset(index: Int): Int = HEADER_SIZE + index * RECORD_SIZE

        fun payloadOffset(frame: ByteArray): Int = getU64(frame, 32).toInt()

        fun getU64(bytes: ByteArray, offset: Int): Long =
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getLong(offset)

        fun putU16(bytes: ByteArray, offset: Int, value: Int) {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort())
        }

        fun putU32(bytes: ByteArray, offset: Int, value: Int) =
            putU32(bytes, offset, value.toLong())

        fun putU32(bytes: ByteArray, offset: Int, value: Long) {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value.toInt())
        }

        fun putU64(bytes: ByteArray, offset: Int, value: Long) {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putLong(offset, value)
        }

        fun ByteArray.mutateAndRefresh(offset: Int, value: Int): ByteArray = copyOf().also {
            it[offset] = value.toByte()
            refreshCrc(it)
        }

        fun ByteArray.putU16AndRefresh(offset: Int, value: Int): ByteArray = copyOf().also {
            putU16(it, offset, value)
            refreshCrc(it)
        }

        fun ByteArray.putU32AndRefresh(offset: Int, value: Int): ByteArray =
            putU32AndRefresh(offset, value.toLong())

        fun ByteArray.putU32AndRefresh(offset: Int, value: Long): ByteArray = copyOf().also {
            putU32(it, offset, value)
            refreshCrc(it)
        }

        fun ByteArray.putU64AndRefresh(offset: Int, value: Long): ByteArray = copyOf().also {
            putU64(it, offset, value)
            refreshCrc(it)
        }

        fun refreshCrc(bytes: ByteArray): ByteArray {
            putU32(bytes, 56, crc32c(bytes, HEADER_SIZE, bytes.size))
            putU32(bytes, 60, crc32c(bytes, 0, 60))
            return bytes
        }

        fun crc32c(bytes: ByteArray, start: Int, end: Int): Long = CRC32C().run {
            update(bytes, start, end - start)
            value
        }
    }
}

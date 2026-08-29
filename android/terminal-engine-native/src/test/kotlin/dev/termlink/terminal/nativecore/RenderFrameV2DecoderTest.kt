package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderScreenV1
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.StandardCharsets
import java.util.zip.CRC32C
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class RenderFrameV2DecoderTest {
    @Test
    fun decodesAllV2CursorShapesAndBlinkingWhileSharingTheV1PayloadModel() {
        val shapes = listOf(
            TerminalRenderCursorShapeV2.BLOCK,
            TerminalRenderCursorShapeV2.UNDERLINE,
            TerminalRenderCursorShapeV2.BAR,
        )
        shapes.forEachIndexed { shapeValue, expectedShape ->
            for (blinkingValue in 0..1) {
                val frame = RenderFrameV2Decoder.decode(
                    FrameBuilder(shapeValue, blinkingValue).build(),
                )

                assertEquals(2, frame.formatVersion)
                assertEquals(7L, frame.presentationRevision)
                assertEquals(11L, frame.nextSourceSequence)
                assertEquals(TerminalRenderScreenV1.ALTERNATE, frame.activeScreen)
                assertTrue(frame.reverseVideo)
                assertEquals(expectedShape, frame.cursor.shape)
                assertEquals(blinkingValue == 1, frame.cursor.blinking)
                assertTrue(frame.cursor.visible)
                assertTrue(frame.cursor.wrapPending)
                assertEquals(listOf("A", "界"), frame.graphemes.map { it.text })
                assertEquals(listOf(1, 2), frame.graphemes.map { it.displayWidth })
                assertEquals(TerminalRenderColorV1.Default, frame.renditions[0].foreground)
                assertIs<TerminalRenderColorV1.Indexed>(frame.renditions[1].foreground)
                assertEquals(0x010203, frame.palette.colorsRgb[1])
                assertNull(frame.palette.colorsRgb[0])
                assertEquals("https://example.test/界", frame.hyperlinks.single().uri)
                assertTrue(frame.rows[0].softWrapped)
                assertFalse(frame.rows[0].cells[0].wideContinuation)
                assertTrue(frame.rows[0].cells[2].wideContinuation)
                assertEquals("mixed 界", frame.title)
                assertFailsWith<UnsupportedOperationException> {
                    @Suppress("UNCHECKED_CAST")
                    (frame.rows as MutableList<Any?>).clear()
                }
            }
        }
    }

    @Test
    fun rejectsV1AndEveryNonCanonicalV2HeaderIdentityField() {
        val baseline = FrameBuilder().build()
        val mutations = listOf(
            baseline.mutateAndRefresh(0, 'X'.code),
            baseline.putU16AndRefresh(8, 1),
            baseline.putU16AndRefresh(10, 1),
            baseline.putU16AndRefresh(12, 88),
            baseline.putU16AndRefresh(14, 16),
            baseline.putU16AndRefresh(16, 5),
        )
        mutations.forEach(::assertRejected)

        val v1 = baseline.copyOf().also {
            MAGIC_V1.copyInto(it, 0)
            putU16(it, 8, 1)
            putU32(it, 68, 0)
            putU64(it, 72, 0x1f)
            refreshCrc(it)
        }
        assertRejected(v1)
    }

    @Test
    fun rejectsUnknownCursorShapeBlinkingReservedAndFeatureValues() {
        listOf(3, 255).forEach { assertRejected(FrameBuilder().build().mutateAndRefresh(68, it)) }
        listOf(2, 255).forEach { assertRejected(FrameBuilder().build().mutateAndRefresh(69, it)) }
        assertRejected(FrameBuilder().build().putU16AndRefresh(70, 1))
        assertRejected(FrameBuilder().build().putU64AndRefresh(72, 0x1f))
        assertRejected(FrameBuilder().build().putU64AndRefresh(72, 0x7f))
        assertRejected(FrameBuilder().build().putU64AndRefresh(72, Long.MIN_VALUE))
    }

    @Test
    fun rejectsEveryInvalidUnchangedHeaderRangeFlagAndReservedContract() {
        val baseline = FrameBuilder().build()
        val mutations = listOf(
            baseline.putU16AndRefresh(18, 0b100),
            baseline.putU32AndRefresh(20, baseline.size - 1),
            baseline.putU32AndRefresh(40, 0),
            baseline.putU32AndRefresh(40, 513),
            baseline.putU32AndRefresh(44, 0),
            baseline.putU32AndRefresh(44, 513),
            baseline.putU32AndRefresh(48, 2),
            baseline.putU32AndRefresh(52, 3),
            baseline.putU32AndRefresh(56, 4),
            baseline.putU32AndRefresh(60, 2),
            baseline.putU32AndRefresh(64, DIRECTORY_OFFSET + 8),
            baseline.putU64AndRefresh(80, 1),
            baseline.putU64AndRefresh(32, Long.MIN_VALUE),
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun rejectsMutationsInBothOriginalV2CrcDomains() {
        val header = FrameBuilder().build().copyOf().also { it[24] = 8 }
        val payload = FrameBuilder().build().copyOf().also {
            it[sectionOffset(it, 6)] = 'X'.code.toByte()
        }
        val payloadCrc = FrameBuilder().build().copyOf().also { it[88] = (it[88] + 1).toByte() }
        val headerCrc = FrameBuilder().build().copyOf().also { it[92] = (it[92] + 1).toByte() }

        listOf(header, payload, payloadCrc, headerCrc).forEach(::assertRejected)
    }

    @Test
    fun rejectsDirectoryOverlapGapMetadataPaddingAndTrailingBytes() {
        val baseline = FrameBuilder().build()
        val overlap = baseline.let { frame ->
            val firstOffset = getU32(frame, DIRECTORY_OFFSET + 8)
            frame.putU32AndRefresh(DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 8, firstOffset)
        }
        val gap = baseline.let { frame ->
            val secondOffset = DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 8
            frame.putU32AndRefresh(secondOffset, getU32(frame, secondOffset) + 8)
        }
        val duplicateTag = baseline.putU32AndRefresh(DIRECTORY_OFFSET, 2)
        val sectionVersion = baseline.putU16AndRefresh(DIRECTORY_OFFSET + 4, 2)
        val nonCritical = baseline.putU16AndRefresh(DIRECTORY_OFFSET + 6, 0)
        val nonzeroPadding = baseline.let { frame ->
            val end = sectionOffset(frame, 1) + sectionLength(frame, 1)
            assertTrue(end < align8(end))
            frame.mutateAndRefresh(end, 1)
        }
        val trailing = baseline.copyOf(baseline.size + 8).also {
            putU32(it, 20, it.size)
            refreshCrc(it)
        }

        listOf(
            overlap,
            gap,
            duplicateTag,
            sectionVersion,
            nonCritical,
            nonzeroPadding,
            trailing,
        ).forEach(::assertRejected)
    }

    @Test
    fun rejectsMalformedPayloadTablesUtf8ReferencesAndWideCells() {
        val baseline = FrameBuilder().build()
        val graphemes = sectionOffset(baseline, 1)
        val renditions = sectionOffset(baseline, 2)
        val palette = sectionOffset(baseline, 3)
        val links = sectionOffset(baseline, 4)
        val cells = firstCellOffset(baseline)
        val mutations = listOf(
            baseline.mutateAndRefresh(graphemes + 6, 0),
            baseline.mutateAndRefresh(graphemes + 7, 1),
            baseline.mutateAndRefresh(graphemes + 16, 0xff),
            baseline.putU32AndRefresh(renditions, 0x80),
            baseline.putU32AndRefresh(renditions + 12, 1),
            baseline.putU32AndRefresh(palette, 0x0100_0000),
            baseline.putU32AndRefresh(links, 0),
            baseline.putU32AndRefresh(links + 12, 1),
            baseline.putU32AndRefresh(cells, 3),
            baseline.putU32AndRefresh(cells + 4, 2),
            baseline.putU32AndRefresh(cells + 8, 6 shl 2),
            baseline.putU32AndRefresh(cells + 2 * 12, 1),
            baseline.putU32AndRefresh(cells + 2 * 12 + 4, 0),
            baseline.putU32AndRefresh(cells + 2 * 12 + 8, 0),
        )

        mutations.forEach(::assertRejected)
    }

    @Test
    fun rejectsNonCanonicalPayloadOrderingAndUnsignedHostOverflows() {
        val baseline = FrameBuilder().build()
        val graphemeOrder = baseline.copyOf().also { frame ->
            val section = sectionOffset(frame, 1)
            val strings = section + 16
            val firstLength = getU16(frame, section + 4)
            val swap = frame[strings]
            frame[strings] = frame[strings + firstLength]
            frame[strings + firstLength] = swap
            refreshCrc(frame)
        }
        val renditionOrder = baseline.copyOf().also { frame ->
            val section = sectionOffset(frame, 2)
            repeat(16) { index ->
                val swap = frame[section + index]
                frame[section + index] = frame[section + 16 + index]
                frame[section + 16 + index] = swap
            }
            refreshCrc(frame)
        }
        val revisionOverflow = baseline.copyOf().also {
            putU64(it, 24, Long.MIN_VALUE)
            refreshCrc(it)
        }
        val lengthOverflow = baseline.putU32AndRefresh(20, 0xffff_ffffL)
        val sectionEndOverflow = baseline.putU32AndRefresh(DIRECTORY_OFFSET + 12, 0xffff_ffffL)

        listOf(
            graphemeOrder,
            renditionOrder,
            revisionOverflow,
            lengthOverflow,
            sectionEndOverflow,
        ).forEach(::assertRejected)
    }

    private fun assertRejected(bytes: ByteArray) {
        assertFailsWith<RenderFrameV2DecodeException> { RenderFrameV2Decoder.decode(bytes) }
    }

    private class FrameBuilder(
        private val cursorShape: Int = 2,
        private val cursorBlinking: Int = 1,
    ) {
        fun build(): ByteArray {
            val graphemeBytes = listOf("A", "界").map { it.toByteArray(StandardCharsets.UTF_8) }
            val graphemes = ByteBuffer.allocate(2 * 8 + graphemeBytes.sumOf { it.size })
                .order(ByteOrder.LITTLE_ENDIAN)
            var stringOffset = 16
            graphemeBytes.forEachIndexed { index, value ->
                graphemes.putInt(index * 8, stringOffset)
                graphemes.putShort(index * 8 + 4, value.size.toShort())
                graphemes.put(index * 8 + 6, (index + 1).toByte())
                stringOffset += value.size
            }
            graphemes.position(16)
            graphemeBytes.forEach(graphemes::put)

            val renditions = ByteBuffer.allocate(32).order(ByteOrder.LITTLE_ENDIAN)
            renditions.putInt(0).putInt(0).putInt(0).putInt(0)
            renditions.putInt(0x61).putInt(0x01000003).putInt(0x02112233).putInt(0)

            val palette = ByteBuffer.allocate(259 * 4).order(ByteOrder.LITTLE_ENDIAN)
            repeat(259) { palette.putInt(-1) }
            palette.putInt(4, 0x010203)
            palette.putInt(256 * 4, 0x00ffffff)
            palette.putInt(257 * 4, 0x00000000)

            val uri = "https://example.test/界".toByteArray(StandardCharsets.UTF_8)
            val links = ByteBuffer.allocate(16 + uri.size).order(ByteOrder.LITTLE_ENDIAN)
            links.putInt(5).putInt(16).putInt(uri.size).putInt(0).put(uri)

            val rows = ByteBuffer.allocate(8 + 2 * 8 + 6 * 12).order(ByteOrder.LITTLE_ENDIAN)
            rows.putInt(8).putInt(12)
            rows.putInt(3).putInt(1)
            rows.putInt(1).putInt(0)
            putCell(rows, 1, 0, 0)
            putCell(rows, 2, 1, (5 shl 2) or 2)
            putCell(rows, 0, 1, (5 shl 2) or 2 or 1)
            putCell(rows, 2, 1, (5 shl 2) or 2)
            putCell(rows, 0, 1, (5 shl 2) or 2 or 1)
            putCell(rows, 1, 0, 0)

            return assemble(
                listOf(
                    SectionBytes(2, 8, graphemes.array()),
                    SectionBytes(2, 16, renditions.array()),
                    SectionBytes(259, 4, palette.array()),
                    SectionBytes(1, 16, links.array()),
                    SectionBytes(6, 0, rows.array()),
                    "mixed 界".toByteArray(StandardCharsets.UTF_8).let {
                        SectionBytes(it.size, 0, it)
                    },
                ),
            )
        }

        private fun putCell(buffer: ByteBuffer, grapheme: Int, rendition: Int, metadata: Int) {
            buffer.putInt(grapheme).putInt(rendition).putInt(metadata)
        }

        private fun assemble(sections: List<SectionBytes>): ByteArray {
            var offset = HEADER_SIZE + SECTION_COUNT * DIRECTORY_ENTRY_SIZE
            val offsets = sections.map { section ->
                offset = align8(offset)
                offset.also { offset = align8(offset + section.bytes.size) }
            }
            val frame = ByteArray(offset)
            MAGIC_V2.copyInto(frame, 0)
            putU16(frame, 8, 2)
            putU16(frame, 10, 0)
            putU16(frame, 12, HEADER_SIZE)
            putU16(frame, 14, DIRECTORY_ENTRY_SIZE)
            putU16(frame, 16, SECTION_COUNT)
            putU16(frame, 18, 0b11)
            putU32(frame, 20, frame.size)
            putU64(frame, 24, 7)
            putU64(frame, 32, 11)
            putU32(frame, 40, 3)
            putU32(frame, 44, 2)
            putU32(frame, 48, 1)
            putU32(frame, 52, 2)
            putU32(frame, 56, 3)
            putU32(frame, 60, 1)
            putU32(frame, 64, DIRECTORY_OFFSET)
            frame[68] = cursorShape.toByte()
            frame[69] = cursorBlinking.toByte()
            putU16(frame, 70, 0)
            putU64(frame, 72, 0x3f)
            sections.forEachIndexed { index, section ->
                val directory = DIRECTORY_OFFSET + index * DIRECTORY_ENTRY_SIZE
                putU32(frame, directory, index + 1)
                putU16(frame, directory + 4, 1)
                putU16(frame, directory + 6, 1)
                putU32(frame, directory + 8, offsets[index])
                putU32(frame, directory + 12, section.bytes.size)
                putU32(frame, directory + 16, section.count)
                putU32(frame, directory + 20, section.recordSize)
                section.bytes.copyInto(frame, offsets[index])
            }
            return refreshCrc(frame)
        }
    }

    private data class SectionBytes(val count: Int, val recordSize: Int, val bytes: ByteArray)

    private companion object {
        const val HEADER_SIZE = 96
        const val DIRECTORY_OFFSET = 96
        const val DIRECTORY_ENTRY_SIZE = 24
        const val SECTION_COUNT = 6
        val MAGIC_V1 = byteArrayOf(
            'T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(), 'F'.code.toByte(),
            'R'.code.toByte(), 'M'.code.toByte(), '1'.code.toByte(), 0,
        )
        val MAGIC_V2 = byteArrayOf(
            'T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(), 'F'.code.toByte(),
            'R'.code.toByte(), 'M'.code.toByte(), '2'.code.toByte(), 0,
        )

        fun firstCellOffset(frame: ByteArray): Int = sectionOffset(frame, 5) + 8 + 2 * 8

        fun sectionOffset(frame: ByteArray, tag: Int): Int =
            getU32(frame, DIRECTORY_OFFSET + (tag - 1) * DIRECTORY_ENTRY_SIZE + 8)

        fun sectionLength(frame: ByteArray, tag: Int): Int =
            getU32(frame, DIRECTORY_OFFSET + (tag - 1) * DIRECTORY_ENTRY_SIZE + 12)

        fun getU16(bytes: ByteArray, offset: Int): Int =
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getShort(offset).toInt() and 0xffff

        fun getU32(bytes: ByteArray, offset: Int): Int =
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt(offset)

        fun putU16(bytes: ByteArray, offset: Int, value: Int) {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort())
        }

        fun putU32(bytes: ByteArray, offset: Int, value: Int) = putU32(bytes, offset, value.toLong())

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
            putU32(bytes, 88, crc32c(bytes, HEADER_SIZE, bytes.size))
            putU32(bytes, 92, crc32c(bytes, 0, 92))
            return bytes
        }

        fun crc32c(bytes: ByteArray, start: Int, end: Int): Long = CRC32C().run {
            update(bytes, start, end - start)
            value
        }

        fun align8(value: Int): Int = (value + 7) and -8
    }
}

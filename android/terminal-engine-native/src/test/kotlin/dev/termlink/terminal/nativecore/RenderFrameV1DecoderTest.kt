package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalRenderColorV1
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

class RenderFrameV1DecoderTest {
    @Test
    fun decodesCanonicalMixedContentIntoImmutableRendererNeutralDtos() {
        val frameBytes = FrameBuilder().build()

        val frame = RenderFrameV1Decoder.decode(frameBytes)

        assertEquals(7L, frame.presentationRevision)
        assertEquals(11L, frame.nextSourceSequence)
        assertEquals(3, frame.size.columns)
        assertEquals(2, frame.size.rows)
        assertEquals(TerminalRenderScreenV1.ALTERNATE, frame.activeScreen)
        assertTrue(frame.reverseVideo)
        assertTrue(frame.cursor.visible)
        assertTrue(frame.cursor.wrapPending)
        assertEquals(1, frame.cursor.row)
        assertEquals(2, frame.cursor.column)
        assertEquals(listOf("A", "界"), frame.graphemes.map { it.text })
        assertEquals(listOf(1, 2), frame.graphemes.map { it.displayWidth })
        assertEquals(TerminalRenderColorV1.Default, frame.renditions[0].foreground)
        assertIs<TerminalRenderColorV1.Indexed>(frame.renditions[1].foreground)
        assertEquals(3, (frame.renditions[1].foreground as TerminalRenderColorV1.Indexed).index)
        assertIs<TerminalRenderColorV1.Rgb>(frame.renditions[1].background)
        assertEquals(0x112233, (frame.renditions[1].background as TerminalRenderColorV1.Rgb).value)
        assertTrue(frame.renditions[1].bold)
        assertTrue(frame.renditions[1].conceal)
        assertTrue(frame.renditions[1].strikethrough)
        assertEquals(0x010203, frame.palette.colorsRgb[1])
        assertNull(frame.palette.colorsRgb[0])
        assertEquals(0xffffff, frame.palette.defaultForegroundRgb)
        assertEquals("https://example.test/界", frame.hyperlinks.single().uri)
        assertEquals(5, frame.hyperlinks.single().reference)
        assertTrue(frame.rows[0].softWrapped)
        assertEquals(3, frame.rows[0].meaningfulEnd)
        assertFalse(frame.rows[0].cells[0].wideContinuation)
        assertTrue(frame.rows[0].cells[1].isProtected)
        assertEquals(5, frame.rows[0].cells[1].hyperlinkReference)
        assertTrue(frame.rows[0].cells[2].wideContinuation)
        assertEquals("mixed 界", frame.title)
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.rows as MutableList<Any?>).clear()
        }
    }

    @Test
    fun rejectsMagicAndBothCrcDomains() {
        assertRejected(FrameBuilder().build().mutateWithoutCrc(0, 'X'.code))
        assertRejected(FrameBuilder().build().mutateWithoutCrc(24, 8))
        assertRejected(FrameBuilder().build().mutateWithoutCrc(sectionOffset(FrameBuilder().build(), 6), 'X'.code))
    }

    @Test
    fun rejectsReservedFieldsAndUnknownBits() {
        val frameFlagsReserved = FrameBuilder().build().copyOf().also {
            putU16(it, 18, 0b111)
            refreshCrc(it)
        }
        assertRejected(frameFlagsReserved)

        val renditionFlagsReserved = FrameBuilder().build().let { frame ->
            val secondRendition = sectionOffset(frame, 2) + 16
            frame.putU32AndRefresh(secondRendition, 0xe1)
        }
        assertRejected(renditionFlagsReserved)

        val headerReserved = FrameBuilder().build().mutateAndRefresh(68, 1)
        assertRejected(headerReserved)

        val graphemeReserved = FrameBuilder().build().let { frame ->
            frame.mutateAndRefresh(sectionOffset(frame, 1) + 7, 1)
        }
        assertRejected(graphemeReserved)

        val metadataReserved = FrameBuilder().build().let { frame ->
            val cells = sectionOffset(frame, 5) + 8 + 2 * 8
            frame.putU32AndRefresh(cells + 8, 1 shl 13)
        }
        assertRejected(metadataReserved)
    }

    @Test
    fun rejectsOverlapGapsAndNonzeroAlignmentPadding() {
        val overlap = FrameBuilder().build().let { frame ->
            val firstOffset = getU32(frame, DIRECTORY_OFFSET + 8)
            frame.putU32AndRefresh(DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 8, firstOffset)
        }
        assertRejected(overlap)

        val gap = FrameBuilder().build().let { frame ->
            val secondOffsetField = DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 8
            frame.putU32AndRefresh(secondOffsetField, getU32(frame, secondOffsetField) + 8)
        }
        assertRejected(gap)

        val padded = FrameBuilder().build().let { frame ->
            val tag1Offset = sectionOffset(frame, 1)
            val tag1Length = sectionLength(frame, 1)
            val padding = tag1Offset + tag1Length
            assertTrue(padding < align8(padding))
            frame.mutateAndRefresh(padding, 1)
        }
        assertRejected(padded)
    }

    @Test
    fun rejectsInvalidUtf8AndPackedColors() {
        val invalidUtf8 = FrameBuilder().build().let { frame ->
            val section = sectionOffset(frame, 1)
            val stringBytes = section + 2 * 8
            frame.mutateAndRefresh(stringBytes, 0xff)
        }
        assertRejected(invalidUtf8)

        val badColor = FrameBuilder().build().let { frame ->
            val secondRendition = sectionOffset(frame, 2) + 16
            frame.putU32AndRefresh(secondRendition + 4, 0x03000000)
        }
        assertRejected(badColor)
    }

    @Test
    fun rejectsDanglingReferencesAndUnreferencedTableEntries() {
        val danglingGrapheme = FrameBuilder().build().let { frame ->
            frame.putU32AndRefresh(firstCellOffset(frame), 3)
        }
        assertRejected(danglingGrapheme)

        val danglingRendition = FrameBuilder().build().let { frame ->
            frame.putU32AndRefresh(firstCellOffset(frame) + 4, 2)
        }
        assertRejected(danglingRendition)

        val danglingLink = FrameBuilder().build().let { frame ->
            frame.putU32AndRefresh(firstCellOffset(frame) + 8, 6 shl 2)
        }
        assertRejected(danglingLink)

        val unusedGrapheme = FrameBuilder().build().let { frame ->
            frame.putU32AndRefresh(firstCellOffset(frame) + 3 * 12, 1)
        }
        assertRejected(unusedGrapheme)
    }

    @Test
    fun rejectsWideContinuationViolations() {
        val missingContinuation = FrameBuilder().build().let { frame ->
            val continuation = firstCellOffset(frame) + 2 * 12
            frame.putU32AndRefresh(continuation, 1)
        }
        assertRejected(missingContinuation)

        val mismatchedStyle = FrameBuilder().build().let { frame ->
            val continuation = firstCellOffset(frame) + 2 * 12
            frame.putU32AndRefresh(continuation + 4, 0)
        }
        assertRejected(mismatchedStyle)

        val standalone = FrameBuilder().build().let { frame ->
            val first = firstCellOffset(frame)
            frame.putU32AndRefresh(first, 0).putU32AndRefresh(first + 8, 1)
        }
        assertRejected(standalone)
    }

    @Test
    fun rejectsNonCanonicalTableOrdering() {
        val graphemeOrder = FrameBuilder().build().let { frame ->
            val section = sectionOffset(frame, 1)
            val firstLength = getU16(frame, section + 4)
            val strings = section + 16
            val first = frame[strings]
            frame[strings] = frame[strings + firstLength]
            frame[strings + firstLength] = first
            refreshCrc(frame)
        }
        assertRejected(graphemeOrder)

        val renditionOrder = FrameBuilder().build().let { frame ->
            val section = sectionOffset(frame, 2)
            repeat(16) { index ->
                val swap = frame[section + index]
                frame[section + index] = frame[section + 16 + index]
                frame[section + 16 + index] = swap
            }
            refreshCrc(frame)
        }
        assertRejected(renditionOrder)
    }

    @Test
    fun rejectsSignedAndCheckedIntegerOverflowBeforeConversion() {
        val u64Overflow = FrameBuilder().build().copyOf().also {
            putU64(it, 24, Long.MIN_VALUE)
            refreshCrc(it)
        }
        assertRejected(u64Overflow)

        val u32RangeOverflow = FrameBuilder().build().putU32AndRefresh(20, 0xffff_ffffL)
        assertRejected(u32RangeOverflow)

        val sectionEndOverflow = FrameBuilder().build().let { frame ->
            frame.putU32AndRefresh(DIRECTORY_OFFSET + 12, 0xffff_ffffL)
        }
        assertRejected(sectionEndOverflow)
    }

    private fun assertRejected(bytes: ByteArray) {
        assertFailsWith<RenderFrameV1DecodeException> { RenderFrameV1Decoder.decode(bytes) }
    }

    private class FrameBuilder {
        fun build(): ByteArray {
            val graphemeBytes = listOf("A", "界").map { it.toByteArray(StandardCharsets.UTF_8) }
            val graphemes = ByteBuffer.allocate(2 * 8 + graphemeBytes.sumOf { it.size })
                .order(ByteOrder.LITTLE_ENDIAN)
            var stringOffset = 16
            graphemeBytes.forEachIndexed { index, value ->
                graphemes.putInt(index * 8, stringOffset)
                graphemes.putShort(index * 8 + 4, value.size.toShort())
                graphemes.put(index * 8 + 6, (index + 1).toByte())
                graphemes.put(index * 8 + 7, 0)
                stringOffset += value.size
            }
            graphemes.position(16)
            graphemeBytes.forEach(graphemes::put)

            val renditions = ByteBuffer.allocate(32).order(ByteOrder.LITTLE_ENDIAN)
            // Default/default rendition sorts before the bold indexed/RGB rendition.
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
                sections = listOf(
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
            MAGIC.copyInto(frame, 0)
            putU16(frame, 8, 1)
            putU16(frame, 10, 0)
            putU16(frame, 12, HEADER_SIZE)
            putU16(frame, 14, DIRECTORY_ENTRY_SIZE)
            putU16(frame, 16, SECTION_COUNT)
            putU16(frame, 18, 0b11)
            putU32(frame, 20, frame.size.toLong())
            putU64(frame, 24, 7)
            putU64(frame, 32, 11)
            putU32(frame, 40, 3)
            putU32(frame, 44, 2)
            putU32(frame, 48, 1)
            putU32(frame, 52, 2)
            putU32(frame, 56, 3)
            putU32(frame, 60, 1)
            putU32(frame, 64, DIRECTORY_OFFSET)
            putU64(frame, 72, 0x1f)
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
            refreshCrc(frame)
            return frame
        }
    }

    private data class SectionBytes(val count: Int, val recordSize: Int, val bytes: ByteArray)

    private companion object {
        const val HEADER_SIZE = 96
        const val DIRECTORY_OFFSET = 96
        const val DIRECTORY_ENTRY_SIZE = 24
        const val SECTION_COUNT = 6
        val MAGIC = byteArrayOf('T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(),
            'F'.code.toByte(), 'R'.code.toByte(), 'M'.code.toByte(), '1'.code.toByte(), 0)

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

        fun ByteArray.mutateWithoutCrc(offset: Int, value: Int): ByteArray = copyOf().also {
            it[offset] = value.toByte()
        }

        fun ByteArray.mutateAndRefresh(offset: Int, value: Int): ByteArray = copyOf().also {
            it[offset] = value.toByte()
            refreshCrc(it)
        }

        fun ByteArray.putU32AndRefresh(offset: Int, value: Int): ByteArray =
            putU32AndRefresh(offset, value.toLong())

        fun ByteArray.putU32AndRefresh(offset: Int, value: Long): ByteArray = copyOf().also {
            putU32(it, offset, value)
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

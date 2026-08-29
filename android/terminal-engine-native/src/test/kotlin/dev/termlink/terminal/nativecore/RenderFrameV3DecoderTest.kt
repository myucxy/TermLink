package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderScreenV1
import dev.termlink.terminal.api.TerminalRenderUnderlineStyleV3
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

class RenderFrameV3DecoderTest {
    @Test
    fun decodesAllV3StylesColorsFlagsAndV2CursorState() {
        val styles = TerminalRenderUnderlineStyleV3.entries
        styles.forEachIndexed { styleValue, expectedStyle ->
            val frame = RenderFrameV3Decoder.decode(FrameBuilder(styleValue).build())

            assertEquals(3, frame.formatVersion)
            assertEquals(7L, frame.presentationRevision)
            assertEquals(11L, frame.nextSourceSequence)
            assertEquals(2, frame.size.columns)
            assertEquals(1, frame.size.rows)
            assertEquals(TerminalRenderScreenV1.ALTERNATE, frame.activeScreen)
            assertTrue(frame.reverseVideo)
            assertEquals(TerminalRenderCursorShapeV2.BAR, frame.cursor.shape)
            assertTrue(frame.cursor.blinking)
            assertTrue(frame.cursor.visible)
            assertTrue(frame.cursor.wrapPending)
            assertEquals(0, frame.cursor.row)
            assertEquals(1, frame.cursor.column)
            assertEquals("A", frame.graphemes.single().text)
            assertEquals(1, frame.graphemes.single().displayWidth)

            val plain = frame.renditions[0]
            assertEquals(TerminalRenderColorV1.Default, plain.foreground)
            assertEquals(TerminalRenderColorV1.Default, plain.background)
            assertEquals(TerminalRenderColorV1.Default, plain.underlineColor)
            assertFalse(plain.bold)
            assertFalse(plain.faint)
            assertFalse(plain.overline)

            val styled = frame.renditions[1]
            assertIs<TerminalRenderColorV1.Indexed>(styled.foreground)
            assertEquals(3, (styled.foreground as TerminalRenderColorV1.Indexed).index)
            assertIs<TerminalRenderColorV1.Rgb>(styled.background)
            assertEquals(0x112233, (styled.background as TerminalRenderColorV1.Rgb).value)
            assertIs<TerminalRenderColorV1.Rgb>(styled.underlineColor)
            assertEquals(0x445566, (styled.underlineColor as TerminalRenderColorV1.Rgb).value)
            assertTrue(styled.bold)
            assertTrue(styled.blink)
            assertTrue(styled.italic)
            assertTrue(styled.inverse)
            assertEquals(expectedStyle, styled.underlineStyle)
            assertEquals(styleValue != 0, styled.underline)
            assertTrue(styled.conceal)
            assertTrue(styled.strikethrough)
            assertTrue(styled.faint)
            assertTrue(styled.overline)

            assertEquals(0x010203, frame.palette.colorsRgb[1])
            assertNull(frame.palette.colorsRgb[0])
            assertEquals("frame v3", frame.title)
            assertEquals(listOf(0, 1), frame.rows.single().cells.map { it.renditionId })
        }
    }

    @Test
    fun decodedFrameIsDetachedFromTheSourceByteArray() {
        val source = FrameBuilder(3).build()
        val frame = RenderFrameV3Decoder.decode(source)

        source.fill(0)

        assertEquals("A", frame.graphemes.single().text)
        assertEquals(TerminalRenderUnderlineStyleV3.CURLY, frame.renditions[1].underlineStyle)
        assertEquals(0x445566, (frame.renditions[1].underlineColor as TerminalRenderColorV1.Rgb).value)
        assertEquals("frame v3", frame.title)
        assertFailsWith<UnsupportedOperationException> {
            @Suppress("UNCHECKED_CAST")
            (frame.rows as MutableList<Any?>).clear()
        }
    }

    @Test
    fun rejectsCrossVersionAndNonCanonicalHeaderIdentity() {
        val baseline = FrameBuilder().build()
        listOf(
            baseline.mutateAndRefresh(0, 'X'.code),
            baseline.putU16AndRefresh(8, 2),
            baseline.putU16AndRefresh(10, 1),
            baseline.putU16AndRefresh(12, 88),
            baseline.putU16AndRefresh(14, 16),
            baseline.putU16AndRefresh(16, 5),
            baseline.putU64AndRefresh(72, 0x3f),
        ).forEach(::assertRejected)

        val v2 = baseline.copyOf().also {
            MAGIC_V2.copyInto(it, 0)
            putU16(it, 8, 2)
            putU64(it, 72, 0x3f)
            putU16(it, DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 4, 1)
            refreshCrc(it)
        }
        assertRejected(v2)
    }

    @Test
    fun rejectsUnknownHeaderFlagsCursorFieldsAndBothCrcDomains() {
        assertRejected(FrameBuilder().build().putU16AndRefresh(18, 4))
        assertRejected(FrameBuilder().build().putU32AndRefresh(56, 4))
        assertRejected(FrameBuilder().build().putU32AndRefresh(52, 0))
        assertRejected(FrameBuilder().build().mutateAndRefresh(68, 3))
        assertRejected(FrameBuilder().build().mutateAndRefresh(69, 2))
        assertRejected(FrameBuilder().build().putU16AndRefresh(70, 1))
        assertRejected(FrameBuilder().build().putU64AndRefresh(80, 1))

        assertRejected(FrameBuilder().build().mutateWithoutCrc(24, 8))
        val payload = FrameBuilder().build()
        assertRejected(payload.mutateWithoutCrc(sectionOffset(payload, 6), 'X'.code))
    }

    @Test
    fun rejectsEveryMixedSectionVersionCriticalFlagAndRenditionRecordSize() {
        repeat(SECTION_COUNT) { index ->
            val expectedVersion = if (index == 1) 2 else 1
            assertRejected(
                FrameBuilder().build().putU16AndRefresh(
                    DIRECTORY_OFFSET + index * DIRECTORY_ENTRY_SIZE + 4,
                    if (expectedVersion == 1) 2 else 1,
                ),
            )
        }
        assertRejected(
            FrameBuilder().build().putU16AndRefresh(
                DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 6,
                0,
            ),
        )
        assertRejected(
            FrameBuilder().build().putU32AndRefresh(
                DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 20,
                12,
            ),
        )
        assertRejected(
            FrameBuilder().build().putU32AndRefresh(
                DIRECTORY_OFFSET + DIRECTORY_ENTRY_SIZE + 12,
                31,
            ),
        )
    }

    @Test
    fun rejectsUnknownRenditionFlagsAndUnderlineStylePresenceMismatches() {
        val second = renditionOffset(FrameBuilder().build(), 1)
        assertRejected(FrameBuilder().build().putU32AndRefresh(second, 1 shl 12))
        assertRejected(FrameBuilder().build().putU32AndRefresh(second, (6 shl 9) or (1 shl 4)))
        assertRejected(FrameBuilder().build().putU32AndRefresh(second, 1 shl 9))
        assertRejected(FrameBuilder().build().putU32AndRefresh(second, 1 shl 4))
    }

    @Test
    fun rejectsIllegalColorsInEveryV3ColorSlot() {
        val second = renditionOffset(FrameBuilder().build(), 1)
        val illegal = listOf(
            0x00000001,
            0x01000100,
            0x03000000,
        )
        listOf(4, 8, 12).forEach { field ->
            illegal.forEach { color ->
                assertRejected(FrameBuilder().build().putU32AndRefresh(second + field, color))
            }
        }
    }

    @Test
    fun rejectsUnsortedDanglingAndUnusedRenditions() {
        val unsorted = FrameBuilder().build().let { frame ->
            val section = sectionOffset(frame, 2)
            repeat(16) { index ->
                val value = frame[section + index]
                frame[section + index] = frame[section + 16 + index]
                frame[section + 16 + index] = value
            }
            refreshCrc(frame)
        }
        assertRejected(unsorted)

        assertRejected(FrameBuilder().build().putU32AndRefresh(firstCellOffset(FrameBuilder().build()) + 4, 2))
        assertRejected(
            FrameBuilder().build().putU32AndRefresh(
                firstCellOffset(FrameBuilder().build()) + CELL_RECORD_SIZE + 4,
                0,
            ),
        )
    }

    @Test
    fun unchangedSectionsRetainStrictCanonicalValidation() {
        val danglingGrapheme = FrameBuilder().build().putU32AndRefresh(firstCellOffset(FrameBuilder().build()), 2)
        assertRejected(danglingGrapheme)

        val graphemeUnusedByte = FrameBuilder().build().let { frame ->
            val directory = DIRECTORY_OFFSET
            frame.putU32AndRefresh(directory + 12, sectionLength(frame, 1) + 1)
        }
        assertRejected(graphemeUnusedByte)

        val nonzeroPadding = FrameBuilder().build().let { frame ->
            val end = sectionOffset(frame, 1) + sectionLength(frame, 1)
            frame.mutateAndRefresh(end, 1)
        }
        assertRejected(nonzeroPadding)
    }

    private fun assertRejected(bytes: ByteArray) {
        assertFailsWith<RenderFrameV3DecodeException> { RenderFrameV3Decoder.decode(bytes) }
    }

    private class FrameBuilder(private val underlineStyle: Int = 5) {
        fun build(): ByteArray {
            val graphemeText = "A".toByteArray(StandardCharsets.UTF_8)
            val graphemes = ByteBuffer.allocate(8 + graphemeText.size).order(ByteOrder.LITTLE_ENDIAN)
            graphemes.putInt(8).putShort(graphemeText.size.toShort()).put(1).put(0).put(graphemeText)

            val renditions = ByteBuffer.allocate(2 * 16).order(ByteOrder.LITTLE_ENDIAN)
            renditions.putInt(0).putInt(0).putInt(0).putInt(0)
            var flags = 0x1ef // Every boolean flag except underline presence.
            if (underlineStyle != 0) flags = flags or (1 shl 4)
            flags = flags or (underlineStyle shl 9)
            renditions.putInt(flags)
                .putInt(0x01000003)
                .putInt(0x02112233)
                .putInt(0x02445566)

            val palette = ByteBuffer.allocate(259 * 4).order(ByteOrder.LITTLE_ENDIAN)
            repeat(259) { palette.putInt(-1) }
            palette.putInt(4, 0x010203)

            val rows = ByteBuffer.allocate(8 + 8 + 2 * CELL_RECORD_SIZE).order(ByteOrder.LITTLE_ENDIAN)
            rows.putInt(8).putInt(CELL_RECORD_SIZE)
            rows.putInt(2).putInt(0)
            putCell(rows, 1, 0, 0)
            putCell(rows, 1, 1, 0)

            return assemble(
                listOf(
                    SectionBytes(1, 8, graphemes.array()),
                    SectionBytes(2, 16, renditions.array()),
                    SectionBytes(259, 4, palette.array()),
                    SectionBytes(0, 16, ByteArray(0)),
                    SectionBytes(2, 0, rows.array()),
                    "frame v3".toByteArray(StandardCharsets.UTF_8).let {
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
            MAGIC_V3.copyInto(frame, 0)
            putU16(frame, 8, 3)
            putU16(frame, 10, 0)
            putU16(frame, 12, HEADER_SIZE)
            putU16(frame, 14, DIRECTORY_ENTRY_SIZE)
            putU16(frame, 16, SECTION_COUNT)
            putU16(frame, 18, 3)
            putU32(frame, 20, frame.size)
            putU64(frame, 24, 7)
            putU64(frame, 32, 11)
            putU32(frame, 40, 2)
            putU32(frame, 44, 1)
            putU32(frame, 48, 0)
            putU32(frame, 52, 1)
            putU32(frame, 56, 3)
            putU32(frame, 60, 1)
            putU32(frame, 64, DIRECTORY_OFFSET)
            frame[68] = 2
            frame[69] = 1
            putU64(frame, 72, 0x7f)
            sections.forEachIndexed { index, section ->
                val directory = DIRECTORY_OFFSET + index * DIRECTORY_ENTRY_SIZE
                putU32(frame, directory, index + 1)
                putU16(frame, directory + 4, if (index == 1) 2 else 1)
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
        const val CELL_RECORD_SIZE = 12
        val MAGIC_V2 = byteArrayOf(
            'T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(), 'F'.code.toByte(),
            'R'.code.toByte(), 'M'.code.toByte(), '2'.code.toByte(), 0,
        )
        val MAGIC_V3 = byteArrayOf(
            'T'.code.toByte(), 'L'.code.toByte(), 'R'.code.toByte(), 'F'.code.toByte(),
            'R'.code.toByte(), 'M'.code.toByte(), '3'.code.toByte(), 0,
        )

        fun firstCellOffset(frame: ByteArray): Int = sectionOffset(frame, 5) + 8 + 8

        fun renditionOffset(frame: ByteArray, index: Int): Int = sectionOffset(frame, 2) + index * 16

        fun sectionOffset(frame: ByteArray, tag: Int): Int =
            getU32(frame, DIRECTORY_OFFSET + (tag - 1) * DIRECTORY_ENTRY_SIZE + 8)

        fun sectionLength(frame: ByteArray, tag: Int): Int =
            getU32(frame, DIRECTORY_OFFSET + (tag - 1) * DIRECTORY_ENTRY_SIZE + 12)

        fun getU32(bytes: ByteArray, offset: Int): Int =
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt(offset)

        fun putU16(bytes: ByteArray, offset: Int, value: Int) {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putShort(offset, value.toShort())
        }

        fun putU32(bytes: ByteArray, offset: Int, value: Int) {
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value)
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

        fun ByteArray.putU16AndRefresh(offset: Int, value: Int): ByteArray = copyOf().also {
            putU16(it, offset, value)
            refreshCrc(it)
        }

        fun ByteArray.putU32AndRefresh(offset: Int, value: Int): ByteArray = copyOf().also {
            putU32(it, offset, value)
            refreshCrc(it)
        }

        fun ByteArray.putU64AndRefresh(offset: Int, value: Long): ByteArray = copyOf().also {
            putU64(it, offset, value)
            refreshCrc(it)
        }

        fun refreshCrc(bytes: ByteArray): ByteArray {
            putU32(bytes, 88, crc32c(bytes, HEADER_SIZE, bytes.size).toInt())
            putU32(bytes, 92, crc32c(bytes, 0, 92).toInt())
            return bytes
        }

        fun crc32c(bytes: ByteArray, start: Int, end: Int): Long = CRC32C().run {
            update(bytes, start, end - start)
            value
        }

        fun align8(value: Int): Int = (value + 7) and -8
    }
}

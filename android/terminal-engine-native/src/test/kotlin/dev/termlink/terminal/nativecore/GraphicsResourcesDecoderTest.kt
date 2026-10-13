package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalGraphicsResource
import dev.termlink.terminal.api.TerminalGraphicsResources
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.CRC32C
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class GraphicsResourcesDecoderTest {
    @Test
    fun decodesRgbaAndPaddedAlphaWithoutChangingPixelChannels() {
        val rgba = byteArrayOf(255.toByte(), 20, 30, 128.toByte(), 40, 50, 60, 0)
        val alpha = byteArrayOf(1, 128.toByte(), 255.toByte())
        val bytes = frame(listOf(Pixels(2, 2, 1, 1, rgba), Pixels(9, 3, 1, 2, alpha)), 11)
        val resources = GraphicsResourcesDecoder.decode(bytes)
        bytes.fill(0)
        assertEquals(11L, resources.nextId)
        assertEquals(11, resources.pixelBytes)
        assertEquals(listOf(2L, 9L), resources.entries.map { it.id })
        assertEquals(TerminalGraphicsFormat.RGBA8888, resources[2]!!.format)
        assertEquals(TerminalGraphicsFormat.A8, resources[9]!!.format)
        assertContentEquals(rgba, resources[2]!!.copyPixels())
        assertContentEquals(alpha, resources[9]!!.copyPixels())
    }

    @Test
    fun decodesEmptyStoreAndPreservesExhaustedUnsignedNextId() {
        val resources = GraphicsResourcesDecoder.decode(frame(emptyList(), -1L))
        assertEquals(-1L, resources.nextId)
        assertTrue(resources.entries.isEmpty())
        assertEquals(0, resources.pixelBytes)
    }

    @Test
    fun acceptsIdsAcrossTheSignedLongBoundaryAndRejectsUnsignedRegression() {
        val resources = GraphicsResourcesDecoder.decode(frame(listOf(alpha(Long.MAX_VALUE), alpha(Long.MIN_VALUE)), -1))
        assertEquals(Long.MIN_VALUE, resources[Long.MIN_VALUE]!!.id)
        rejects(frame(listOf(alpha(Long.MIN_VALUE), alpha(Long.MAX_VALUE)), -1))
        rejects(frame(listOf(alpha(-1)), -1))
    }

    @Test
    fun inspectValidatesEmbeddedRangeAndExposesOnlyMetadataBeforeMaterializing() {
        val bytes = frame(listOf(alpha(4, byteArrayOf(10, 20, 30))), 8)
        val embedded = byteArrayOf(1, 2, 3) + bytes + byteArrayOf(4, 5)
        val inspected = GraphicsResourcesDecoder.inspect(embedded, 3, bytes.size)
        assertEquals(listOf(4L), inspected.ids())
        assertEquals(GraphicsResourcesDecoder.Metadata(3, 1, TerminalGraphicsFormat.A8), inspected.metadata(4))
        assertNull(inspected.metadata(5))
        assertContentEquals(byteArrayOf(10, 20, 30), inspected.materialize()[4]!!.copyPixels())
        for ((offset, length) in listOf(-1 to 48, 0 to -1, Int.MAX_VALUE to 48, 3 to Int.MAX_VALUE)) {
            assertFailsWith<GraphicsResourcesDecodeException> { GraphicsResourcesDecoder.inspect(embedded, offset, length) }
        }
    }

    @Test
    fun materializeRejectsChangedBorrowEvenWhenNewChecksumsAreValid() {
        val bytes = frame(listOf(alpha(1)), 3)
        val inspected = GraphicsResourcesDecoder.inspect(bytes)
        bytes[80] = 42
        put32(bytes, 72, crc(bytes, 80, 81))
        seal(bytes)
        assertFailsWith<GraphicsResourcesDecodeException> { inspected.materialize() }
        assertContentEquals(byteArrayOf(42), GraphicsResourcesDecoder.decode(bytes)[1]!!.copyPixels())
    }

    @Test
    fun rejectsFrameCrcAndPayloadCrcIndependently() {
        val original = frame(listOf(alpha(1, "123456789".toByteArray())), 2)
        assertEquals(0xe3069283L, get32(original, 72))
        rejects(original.copyOf().also { it[80] = 0 })
        rejects(original.copyOf().also { it[80] = 0; seal(it) })
        rejects(original.copyOf().also { put32(it, 40, 0) })
    }

    @Test
    fun rejectsHeaderVersionsReservedFieldsAndIncorrectTotals() {
        val original = frame(listOf(alpha(1)), 2)
        for ((offset, value) in listOf(0 to 0L, 8 to 2L, 10 to 52L, 12 to 0xffff_ffffL,
            24 to 257L, 28 to 1L, 44 to 1L)) {
            rejects(original.copyOf().also { put32(it, offset, value); seal(it) })
        }
        for ((offset, value) in listOf(16 to 0L, 32 to 0L, 32 to -1L,
            32 to TerminalGraphicsResources.MAX_PIXEL_BYTES + 1L)) {
            rejects(original.copyOf().also { put64(it, offset, value); seal(it) })
        }
    }

    @Test
    fun rejectsZeroDuplicateDescendingAndOutOfRangeIds() {
        for (entries in listOf(listOf(alpha(0)), listOf(alpha(1), alpha(1)), listOf(alpha(2), alpha(1)), listOf(alpha(3)))) {
            rejects(frame(entries, 3))
        }
    }

    @Test
    fun rejectsUnknownFormatsResourceReservedFieldsAndPadding() {
        val original = frame(listOf(alpha(1)), 2)
        for (offset in listOf(64, 65, 66, 67, 76, 81, 82, 83)) {
            rejects(original.copyOf().also { it[offset] = 3; seal(it) })
        }
        rejects(original.copyOf().also { it[64] = 0; seal(it) })
    }

    @Test
    fun rejectsDimensionsLengthOverflowAndSingleResourceQuota() {
        val original = frame(listOf(alpha(1)), 2)
        for (offset in listOf(56, 60)) {
            for (value in listOf(0L, 4097L, 0xffff_ffffL)) {
                rejects(original.copyOf().also { put32(it, offset, value); seal(it) })
            }
        }
        for (value in listOf(0L, 2L, 0x8000_0000L, 0xffff_ffffL)) {
            rejects(original.copyOf().also { put32(it, 68, value); seal(it) })
        }
        rejects(original.copyOf().also {
            put32(it, 56, 4096); put32(it, 60, 4096); it[64] = 1
            put32(it, 68, 4096L * 4096 * 4); seal(it)
        })
    }

    @Test
    fun enforcesExactResourceCountAndSessionPixelLimits() {
        val maximumEntries = List(256) { alpha(it + 1L) }
        assertEquals(256, GraphicsResourcesDecoder.decode(frame(maximumEntries, 257)).entries.size)
        rejects(frame(maximumEntries + alpha(257), 258))
        val large = ByteArray(TerminalGraphicsResource.MAX_PIXEL_BYTES)
        val bytes = frame(listOf(Pixels(1, 4096, 4096, 2, large), Pixels(2, 4096, 4096, 2, large)), 4)
        val admitted = GraphicsResourcesDecoder.inspect(bytes)
        assertEquals(TerminalGraphicsResources.MAX_PIXEL_BYTES, admitted.pixelBytes)
        val overBudget = frame(listOf(Pixels(1, 4096, 4096, 2, large), Pixels(2, 4096, 4096, 2, large), alpha(3)), 4)
        // Lie in the aggregate header to exercise the checked per-entry running total.
        put64(overBudget, 32, TerminalGraphicsResources.MAX_PIXEL_BYTES.toLong())
        seal(overBudget)
        rejects(overBudget)
        rejects(ByteArray(TerminalGraphicsResources.MAX_FRAME_BYTES + 1))
    }

    @Test
    fun rejectsEveryTruncationAndTrailingBytesEvenWithAnUpdatedHeaderAndCrc() {
        val original = frame(listOf(alpha(1, byteArrayOf(1, 2, 3)), alpha(2)), 3)
        for (length in original.indices) {
            val truncated = original.copyOf(length)
            if (length >= 48) { put32(truncated, 12, length.toLong()); seal(truncated) }
            rejects(truncated)
        }
        rejects(original.copyOf(original.size + 1).also {
            put32(it, 12, it.size.toLong()); seal(it)
        })
    }

    @Test
    fun lateInvalidEntryIsRejectedByInspectionBeforePixelMaterialization() {
        val bytes = frame(listOf(alpha(1), alpha(2)), 3)
        bytes[84 + 17] = 1
        seal(bytes)
        assertFailsWith<GraphicsResourcesDecodeException> { GraphicsResourcesDecoder.inspect(bytes) }
    }

    private data class Pixels(val id: Long, val width: Int, val height: Int, val format: Int, val bytes: ByteArray)
    private fun alpha(id: Long, bytes: ByteArray = byteArrayOf(127)) = Pixels(id, bytes.size, 1, 2, bytes)

    private fun frame(entries: List<Pixels>, nextId: Long): ByteArray {
        val size = 48 + entries.sumOf { 32 + ((it.bytes.size + 3) and -4) }
        val bytes = ByteArray(size)
        "TLGRS1\u0000\u0000".toByteArray().copyInto(bytes)
        val writer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        writer.putShort(8, 1); writer.putShort(10, 48)
        put32(bytes, 12, size.toLong()); put64(bytes, 16, nextId)
        put32(bytes, 24, entries.size.toLong()); put64(bytes, 32, entries.sumOf { it.bytes.size.toLong() })
        var cursor = 48
        for (entry in entries) {
            put64(bytes, cursor, entry.id); put32(bytes, cursor + 8, entry.width.toLong())
            put32(bytes, cursor + 12, entry.height.toLong()); bytes[cursor + 16] = entry.format.toByte()
            put32(bytes, cursor + 20, entry.bytes.size.toLong())
            put32(bytes, cursor + 24, crc(entry.bytes, 0, entry.bytes.size))
            entry.bytes.copyInto(bytes, cursor + 32)
            cursor += 32 + ((entry.bytes.size + 3) and -4)
        }
        seal(bytes)
        return bytes
    }

    private fun rejects(bytes: ByteArray) {
        assertFailsWith<GraphicsResourcesDecodeException> { GraphicsResourcesDecoder.decode(bytes) }
    }

    private fun put32(bytes: ByteArray, offset: Int, value: Long) {
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putInt(offset, value.toInt())
    }

    private fun put64(bytes: ByteArray, offset: Int, value: Long) {
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).putLong(offset, value)
    }

    private fun get32(bytes: ByteArray, offset: Int) =
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt(offset).toLong() and 0xffff_ffffL

    private fun crc(bytes: ByteArray, start: Int, end: Int): Long = CRC32C().apply { update(bytes, start, end - start) }.value

    private fun seal(bytes: ByteArray) {
        val crc = CRC32C().apply { update(bytes, 0, 40); update(bytes, 44, bytes.size - 44) }
        put32(bytes, 40, crc.value)
    }
}

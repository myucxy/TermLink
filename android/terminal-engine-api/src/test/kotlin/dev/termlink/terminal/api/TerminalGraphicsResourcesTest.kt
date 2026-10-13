package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.nio.ReadOnlyBufferException
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertSame

class TerminalGraphicsResourcesTest {
    @Test
    fun resourceOwnsPixelsAndReadOnlyViewsCannotExposeTheBackingArray() {
        val source = byteArrayOf(10, 20, 30, 40)
        val resource = TerminalGraphicsResource(1, 1, 1, TerminalGraphicsFormat.RGBA8888, source)
        source.fill(0)
        resource.copyPixels().fill(0)
        val view = resource.readOnlyPixels()
        assertFalse(view.hasArray())
        assertFailsWith<ReadOnlyBufferException> { view.put(0, 0) }
        assertFailsWith<ReadOnlyBufferException> { view.array() }
        assertEquals(10.toByte(), view.get())
        assertEquals(0, resource.readOnlyPixels().position())
        assertContentEquals(byteArrayOf(10, 20, 30, 40), resource.copyPixels())
    }

    @Test
    fun copiesOnlyTheBufferRemainingRangeWithoutMovingItsPosition() {
        val buffer = ByteBuffer.wrap(byteArrayOf(99, 2, 3, 88)).apply { position(1); limit(3) }
        val resource = TerminalGraphicsResource(7, 2, 1, TerminalGraphicsFormat.A8, buffer)
        assertEquals(1, buffer.position())
        buffer.put(1, 0)
        assertContentEquals(byteArrayOf(2, 3), resource.copyPixels())
    }

    @Test
    fun containerOwnsItsListAndUsesUnsignedIdsWithoutCopyingSharedResources() {
        val first = alpha(Long.MAX_VALUE)
        val second = alpha(Long.MIN_VALUE)
        val list = mutableListOf(first, second)
        val resources = TerminalGraphicsResources(-1, list)
        list.clear()
        assertEquals(2, resources.pixelBytes)
        assertSame(first, resources[Long.MAX_VALUE])
        assertSame(second, resources[Long.MIN_VALUE])
        assertNull(resources[0])
        assertFailsWith<UnsupportedOperationException> {
            (resources.entries as MutableList).clear()
        }
        assertSame(first, TerminalGraphicsResources(-1, resources.entries).entries.first())
    }

    @Test
    fun constructorsRejectInvalidIdsOrderingAndCounts() {
        assertFailsWith<IllegalArgumentException> { alpha(0) }
        assertFailsWith<IllegalArgumentException> { TerminalGraphicsResources(0, emptyList()) }
        for (entries in listOf(listOf(alpha(1), alpha(1)), listOf(alpha(2), alpha(1)), listOf(alpha(3)))) {
            assertFailsWith<IllegalArgumentException> { TerminalGraphicsResources(3, entries) }
        }
        assertFailsWith<IllegalArgumentException> { TerminalGraphicsResources(-1, List(257) { alpha(it + 1L) }) }
        assertEquals(0, TerminalGraphicsResources(-1, emptyList()).pixelBytes)
    }

    @Test
    fun dimensionsAndExactPixelLengthAreCheckedBeforeCopying() {
        for ((width, height) in listOf(0 to 1, 1 to 0, 4097 to 1, 1 to 4097, Int.MAX_VALUE to Int.MAX_VALUE)) {
            assertFailsWith<IllegalArgumentException> {
                TerminalGraphicsResource(1, width, height, TerminalGraphicsFormat.A8, byteArrayOf(0))
            }
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalGraphicsResource(1, 4096, 4096, TerminalGraphicsFormat.RGBA8888, byteArrayOf(0))
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalGraphicsResource(1, 1, 1, TerminalGraphicsFormat.RGBA8888, ByteArray(3))
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalGraphicsResource(1, 1, 1, TerminalGraphicsFormat.A8, ByteArray(2))
        }
    }

    @Test
    fun sessionAcceptsTheExactBudgetAndRejectsOneMorePixel() {
        val pixels = ByteArray(TerminalGraphicsResource.MAX_PIXEL_BYTES)
        val first = TerminalGraphicsResource(1, 4096, 4096, TerminalGraphicsFormat.A8, pixels)
        val second = TerminalGraphicsResource(2, 4096, 4096, TerminalGraphicsFormat.A8, pixels)
        assertEquals(TerminalGraphicsResources.MAX_PIXEL_BYTES, TerminalGraphicsResources(3, listOf(first, second)).pixelBytes)
        assertFailsWith<IllegalArgumentException> {
            TerminalGraphicsResources(4, listOf(first, second, alpha(3)))
        }
    }

    private fun alpha(id: Long) = TerminalGraphicsResource(id, 1, 1, TerminalGraphicsFormat.A8, byteArrayOf(127))
}

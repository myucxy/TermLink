package dev.termlink.terminal.api

import org.junit.Test
import org.junit.Assert.assertEquals
import kotlin.test.assertFailsWith

class TerminalHostGeometryV1Test {
    @Test fun geometryWireBoundsRejectHalfKnownPairsReservedWordsAndWrongVersions() {
        val value = TerminalHostGeometryV1(9,18,1920,1080,744,476)
        assertEquals(value, TerminalHostGeometryV1.decode(value.encode()))
        assertEquals(TerminalHostGeometryV1(), TerminalHostGeometryV1.decode(TerminalHostGeometryV1().encode()))
        for ((offset, byte) in listOf(0 to 2, 28 to 1, 4 to 0)) {
            val bytes = value.encode().also { it[offset] = byte.toByte() }
            assertFailsWith<IllegalArgumentException> { TerminalHostGeometryV1.decode(bytes) }
        }
        assertFailsWith<IllegalArgumentException> { TerminalHostGeometryV1.decode(ByteArray(33)) }
        assertFailsWith<IllegalArgumentException> { TerminalHostGeometryV1(1025,1) }
        assertFailsWith<IllegalArgumentException> { TerminalHostGeometryV1(displayWidth = 32769, displayHeight = 1) }
        assertFailsWith<IllegalArgumentException> { TerminalHostGeometryV1(windowWidth = -1, windowHeight = 1) }
    }
}

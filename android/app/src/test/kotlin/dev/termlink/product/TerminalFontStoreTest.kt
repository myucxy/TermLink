package dev.termlink.product

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalFontStoreTest {
    @Test
    fun acceptsTrueTypeAndOpenTypeHeadersOnly() {
        assertTrue(hasSupportedOpenTypeHeader(byteArrayOf(0x00, 0x01, 0x00, 0x00)))
        assertTrue(hasSupportedOpenTypeHeader("OTTO".encodeToByteArray()))
        assertTrue(hasSupportedOpenTypeHeader("true".encodeToByteArray()))
        assertTrue(hasSupportedOpenTypeHeader("typ1".encodeToByteArray()))
        assertFalse(hasSupportedOpenTypeHeader("ttcf".encodeToByteArray()))
        assertFalse(hasSupportedOpenTypeHeader(byteArrayOf(0x00, 0x01)))
    }

    @Test
    fun customFontDisplayNamesAreBoundedAndDoNotRetainPaths() {
        assertEquals("Font.ttf", normalizeTerminalFontDisplayName("/storage/shared/Font.ttf"))
        assertEquals("Font.otf", normalizeTerminalFontDisplayName("C:\\Fonts\\Font.otf"))
        assertEquals("Custom font", normalizeTerminalFontDisplayName("   "))
        assertEquals(96, normalizeTerminalFontDisplayName("x".repeat(120)).length)
    }
}

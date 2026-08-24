package dev.termlink.product

import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
class TerminalColorPickerTest {
    @Test
    fun hsvRoundTripPreservesOpaqueTerminalColor() {
        val original = 0xFF65D6A1.toInt()
        val hsv = terminalColorHsv(original)

        assertEquals(original, opaqueArgbFromHsv(hsv[0], hsv[1], hsv[2]))
        assertEquals("#65D6A1", terminalColorHex(original))
    }

    @Test
    fun pickerOutputIsOpaqueAndClampsInvalidCoordinates() {
        assertEquals(0xFFFF0000.toInt(), opaqueArgbFromHsv(-10f, 2f, 2f))
        assertEquals(0xFF000000.toInt(), opaqueArgbFromHsv(400f, -1f, -1f))
    }
}

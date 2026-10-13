package dev.termlink.terminal.api

import org.junit.Test
import org.junit.Assert.*

class TerminalHostSignalsTest {
    @Test fun clipboardIsStrictUtf8WithExactLengthAndNoNul() {
        fun frame(text: ByteArray): ByteArray = java.nio.ByteBuffer.allocate(8 + text.size).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            .put(byteArrayOf(3,0,0,0)).putInt(text.size).put(text).array()
        assertEquals(TerminalPlatformAction.ClipboardWrite("中文\n"), TerminalPlatformAction.decode(frame("中文\n".toByteArray())))
        assertEquals(TerminalPlatformAction.ClipboardWrite(""), TerminalPlatformAction.decode(frame(byteArrayOf())))
        for (bad in listOf(frame(byteArrayOf(0)),frame(byteArrayOf(-1)),frame("x".toByteArray()).also { it[4] = 2 },
            frame("x".toByteArray()).also { it[1] = 1 },frame(ByteArray(16385) { 65 })))
            assertTrue(runCatching { TerminalPlatformAction.decode(bad) }.isFailure)
        TerminalHostEffectV2(TerminalHostEffectKindV2.PLATFORM_ACTION, 1, 1, 0, frame(ByteArray(16384) { 65 }))
    }
    @Test fun progressAndSoundRejectNoncanonicalPayloads() {
        val progress = byteArrayOf(84,76,72,83,49,0,0,0,1,100,0,0)
        assertEquals(TerminalHostSignals(1,100), TerminalHostSignals.decode(progress))
        for ((index,value) in listOf(0 to 0,8 to 5,9 to 101,10 to 1))
            assertTrue(runCatching { TerminalHostSignals.decode(progress.copyOf().also { it[index] = value.toByte() }) }.isFailure)
        val bell = byteArrayOf(1,0,0,0)
        assertEquals(TerminalPlatformAction.Bell, TerminalPlatformAction.decode(bell))
        val sound = byteArrayOf(2,7,-1,3,1,0,25)
        assertEquals(TerminalPlatformAction.Sound(7,255,listOf(1,0,25)),TerminalPlatformAction.decode(sound))
        for (bytes in listOf(byteArrayOf(1,0,0,1),byteArrayOf(2,8,1,1,1),byteArrayOf(2,1,1,0),byteArrayOf(2,1,1,1,26),sound.copyOf(6)))
            assertTrue(runCatching { TerminalPlatformAction.decode(bytes) }.isFailure)
        TerminalHostEffectV2(TerminalHostEffectKindV2.PLATFORM_ACTION,1,1,0,bell)
    }
}

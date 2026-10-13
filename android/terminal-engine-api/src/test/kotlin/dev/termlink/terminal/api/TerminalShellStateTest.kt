package dev.termlink.terminal.api

import org.junit.Test
import org.junit.Assert.*
import java.nio.ByteBuffer
import java.nio.ByteOrder

class TerminalShellStateTest {
    private fun fixture(): ByteArray {
        val cwd = "file:///C:/work/%E4%B8%AD%20test".toByteArray()
        val b = ByteBuffer.allocate(32 + cwd.size + 116 + cwd.size).order(ByteOrder.LITTLE_ENDIAN)
        b.put(byteArrayOf(84,76,83,72,86,49,0,0)).putLong(2).putInt(cwd.size).putInt(1).putLong(0).put(cwd)
        b.putLong(1).put(3).put(15).putShort(0).putInt(-1)
        repeat(4) { b.putLong(4).putLong(0).putInt(it * 2).put(1).put(0).putShort(0) }
        return b.putInt(cwd.size).put(cwd).array()
    }
    @Test fun strictStateAndAllTruncatedBoundaries() {
        val bytes = fixture()
        val state = TerminalShellState.decode(bytes, 5)
        assertEquals(-1, state.commands.single().exitCode)
        assertEquals("C:\\work\\中 test", TerminalShellState.localWindowsDirectory(state.cwdUri))
        for (end in bytes.indices) assertTrue(runCatching { TerminalShellState.decode(bytes.copyOf(end), 5) }.isFailure)
        assertTrue(runCatching { TerminalShellState.decode(bytes, 4) }.isFailure)
        for ((at,value) in listOf(24 to 1, 20 to 65, 8 to 0)) {
            assertTrue(runCatching { TerminalShellState.decode(bytes.copyOf().also { it[at] = value.toByte() }, 5) }.isFailure)
        }
    }
    @Test fun directoryActionsRejectNetworkDeviceTraversalAndInvalidEncoding() {
        for (uri in listOf("file://remote/C:/secret", "file:////server/share", "file:///C:/a%5Cb", "file:///C:/a%00",
            "file:///C:/a%FF", "file:///C:/a%3Ab", "file:///C:/../Windows", "file:///C:/a.", "https://example.test/a")) {
            assertNull(uri, TerminalShellState.localWindowsDirectory(uri))
        }
        assertEquals("C:\\", TerminalShellState.localWindowsDirectory("file:///C:/"))
    }
}

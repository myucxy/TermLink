package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertFailsWith

class TerminalCoreInputTest {
    @Test
    fun encodesUnicodeAndTerminalKeysDeterministically() {
        assertContentEquals(
            "中文🙂".encodeToByteArray(),
            VtTerminalInputEncoder.encode(TerminalInput.Text("中文🙂")),
        )
        assertContentEquals(
            byteArrayOf(0x03),
            VtTerminalInputEncoder.encode(
                TerminalInput.Text("c", TerminalInputModifiers(control = true)),
            ),
        )
        assertContentEquals(
            "\u001b[1;7A".encodeToByteArray(),
            VtTerminalInputEncoder.encode(
                TerminalInput.Key(
                    TerminalKey.ARROW_UP,
                    TerminalInputModifiers(control = true, alt = true),
                ),
            ),
        )
    }

    @Test
    fun rawInputPreservesEveryByteAndReturnsACopy() {
        val source = byteArrayOf(0x00, 0x1b, 0x7f, 0xff.toByte())
        val encoded = VtTerminalInputEncoder.encode(TerminalInput.RawBytes(source))
        assertContentEquals(source, encoded)
        source.fill(0)
        assertContentEquals(byteArrayOf(0x00, 0x1b, 0x7f, 0xff.toByte()), encoded)
    }

    @Test
    fun rejectsInvalidControlText() {
        assertFailsWith<IllegalArgumentException> {
            VtTerminalInputEncoder.encode(
                TerminalInput.Text("中文", TerminalInputModifiers(control = true)),
            )
        }
    }
}

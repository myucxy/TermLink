package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalSize
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class NativeInputV1ContractTest {
    @Test
    fun semanticInputIsOwnedAndMarshalledThroughInputV1() {
        val bridge = FakeNativeBridge()
        NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1).use { terminal ->
            assertContentEquals(
                byteArrayOf(3),
                terminal.encodeCharacter(
                    'c'.code,
                    TerminalInputModifiers(control = true),
                ),
            )
            assertEquals(NATIVE_INPUT_KIND_KEY, bridge.lastInputV1?.kind)
            assertEquals(NATIVE_INPUT_KEY_CHARACTER.toInt(), bridge.lastInputV1?.key)
            assertEquals('c'.code, bridge.lastInputV1?.character)

            assertContentEquals(
                "\u001b[1;3A".encodeToByteArray(),
                terminal.encodeKey(TerminalKey.ARROW_UP, TerminalInputModifiers(alt = true)),
            )
            assertEquals(12, bridge.lastInputV1?.key)

            val paste = "中文🙂".encodeToByteArray()
            val encoded = terminal.encodePaste(paste)
            paste.fill(0)
            assertContentEquals("中文🙂".encodeToByteArray(), encoded)
            assertContentEquals("中文🙂".encodeToByteArray(), bridge.lastInputV1?.copyData())

            assertContentEquals(byteArrayOf(), terminal.encodeFocus(true))
            assertEquals(NATIVE_INPUT_KIND_FOCUS_GAINED, bridge.lastInputV1?.kind)
            assertContentEquals(byteArrayOf(), terminal.encodeFocus(false))
            assertEquals(NATIVE_INPUT_KIND_FOCUS_LOST, bridge.lastInputV1?.kind)
            assertEquals(5, bridge.inputV1Calls)
        }
    }

    @Test
    fun invalidUnicodeAndEmptyPasteNeverCrossTheBridge() {
        val bridge = FakeNativeBridge()
        NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1).use { terminal ->
            assertFailsWith<IllegalArgumentException> {
                terminal.encodeCharacter(Character.MIN_SURROGATE.code)
            }
            assertFailsWith<IllegalArgumentException> { terminal.encodePaste(byteArrayOf()) }
            assertEquals(0, bridge.inputV1Calls)
        }
    }
}

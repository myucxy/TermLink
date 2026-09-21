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
    fun keyboardUsesInputV3WhilePasteAndFocusRemainInputV1() {
        val bridge = FakeNativeBridge()
        NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1).use { terminal ->
            terminal.encodeCharacter(
                'c'.code,
                TerminalInputModifiers(control = true),
            )
            assertEquals(NATIVE_INPUT_KEY_CHARACTER, bridge.lastInputV3?.key)
            assertEquals('c'.code, bridge.lastInputV3?.character)
            assertEquals(NATIVE_KITTY_MODIFIER_CONTROL, bridge.lastInputV3?.kittyModifiers)

            terminal.encodeKey(TerminalKey.ARROW_UP, TerminalInputModifiers(alt = true))
            assertEquals(12L, bridge.lastInputV3?.key)
            assertEquals(NATIVE_KITTY_MODIFIER_ALT, bridge.lastInputV3?.kittyModifiers)

            val paste = "中文🙂".encodeToByteArray()
            val encoded = terminal.encodePaste(paste)
            paste.fill(0)
            assertContentEquals("中文🙂".encodeToByteArray(), encoded)
            assertContentEquals("中文🙂".encodeToByteArray(), bridge.lastInputV1?.copyData())

            assertContentEquals(byteArrayOf(), terminal.encodeFocus(true))
            assertEquals(NATIVE_INPUT_KIND_FOCUS_GAINED, bridge.lastInputV1?.kind)
            assertContentEquals(byteArrayOf(), terminal.encodeFocus(false))
            assertEquals(NATIVE_INPUT_KIND_FOCUS_LOST, bridge.lastInputV1?.kind)
            assertEquals(2, bridge.inputV3Calls)
            assertEquals(3, bridge.inputV1Calls)
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
            assertEquals(0, bridge.inputV3Calls)
        }
    }
}

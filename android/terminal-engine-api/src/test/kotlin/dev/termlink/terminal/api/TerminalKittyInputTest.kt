package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull

class TerminalKittyInputTest {
    @Test
    fun characterInputPreservesAllSemanticFieldsAndStrictUtf8() {
        val input = TerminalKittyKeyInput(
            key = TerminalKittyKey.Character('a'.code),
            modifiers = TerminalKittyModifiers(
                shift = true,
                alt = true,
                control = true,
                superKey = true,
                hyper = true,
                meta = true,
                capsLock = true,
                numLock = true,
            ),
            eventType = TerminalKittyKeyEventType.REPEAT,
            shiftedKey = 'A'.code,
            baseLayoutKey = 'q'.code,
            associatedText = "é🙂",
        )

        assertContentEquals("é🙂".encodeToByteArray(), input.copyAssociatedTextUtf8())
        val first = input.copyAssociatedTextUtf8()
        first.fill(0)
        assertContentEquals("é🙂".encodeToByteArray(), input.copyAssociatedTextUtf8())
    }

    @Test
    fun invalidScalarsControlsAndMalformedUtf16AreRejectedLocally() {
        listOf(-1, 0x11_0000, 0xd800, 0, 0x1f, 0x7f, 0x9f).forEach { scalar ->
            assertFailsWith<IllegalArgumentException> {
                TerminalKittyKey.Character(scalar)
            }
        }
        val malformedUtf16 = assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Character('a'.code),
                associatedText = String(charArrayOf('\ud800')),
            )
        }
        assertNotNull(malformedUtf16.cause)
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Character('a'.code),
                associatedText = "ok\u0080",
            )
        }
    }

    @Test
    fun structuralKittyRulesAndAssociatedTextBoundsAreEnforced() {
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Character('a'.code),
                shiftedKey = 'A'.code,
            )
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Functional(TerminalKittyFunctionalKey.F13),
                baseLayoutKey = 'a'.code,
            )
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Character('a'.code),
                eventType = TerminalKittyKeyEventType.RELEASE,
                associatedText = "a",
            )
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(key = TerminalKittyKey.UnidentifiedText)
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.UnidentifiedText,
                eventType = TerminalKittyKeyEventType.REPEAT,
                associatedText = "a",
            )
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Character('a'.code),
                associatedText = "a".repeat(TerminalKittyKeyInput.MAX_ASSOCIATED_TEXT_SCALARS + 1),
            )
        }
        val maximumFourByteText = "🙂".repeat(TerminalKittyKeyInput.MAX_ASSOCIATED_TEXT_SCALARS)
        TerminalKittyKeyInput(
            key = TerminalKittyKey.Character('a'.code),
            associatedText = maximumFourByteText,
        )
        assertFailsWith<IllegalArgumentException> {
            TerminalKittyKeyInput(
                key = TerminalKittyKey.Character('a'.code),
                associatedText = maximumFourByteText + "a",
            )
        }
        TerminalKittyKeyInput(
            key = TerminalKittyKey.UnidentifiedText,
            associatedText = "a".repeat(TerminalKittyKeyInput.MAX_ASSOCIATED_TEXT_SCALARS),
        )
    }
}

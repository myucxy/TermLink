package dev.termlink.terminal.api

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs

class TerminalMouseInputTest {
    @Test
    fun actionTypesCarryOnlyTheirSemanticPayload() {
        val position = TerminalMousePosition(
            cell = TerminalCellPosition(column = 3, row = 1),
            pixel = TerminalPixelPosition(x = 49, y = 29),
        )
        val held = TerminalMouseButtons(left = true, right = true)

        val press = TerminalMouseInput.Press(
            button = TerminalMouseButton.LEFT,
            position = position,
            buttonsHeld = held,
            modifiers = TerminalInputModifiers(control = true, shift = true),
        )
        val release = TerminalMouseInput.Release(
            button = TerminalMouseButton.MIDDLE,
            position = position,
            buttonsHeld = held,
        )
        val move = TerminalMouseInput.Move(position, held)
        val wheel = TerminalMouseInput.Wheel(
            TerminalMouseWheelDirection.DOWN,
            position,
            held,
        )

        assertEquals(TerminalMouseButton.LEFT, assertIs<TerminalMouseInput.Press>(press).button)
        assertEquals(TerminalMouseButton.MIDDLE, assertIs<TerminalMouseInput.Release>(release).button)
        assertEquals(position, assertIs<TerminalMouseInput.Move>(move).position)
        assertEquals(
            TerminalMouseWheelDirection.DOWN,
            assertIs<TerminalMouseInput.Wheel>(wheel).direction,
        )
        assertEquals(position, press.position)
        assertEquals(held, press.buttonsHeld)
    }

    @Test
    fun rejectsNegativeCoordinatesAndInconsistentButtonState() {
        assertFailsWith<IllegalArgumentException> { TerminalCellPosition(-1, 0) }
        assertFailsWith<IllegalArgumentException> { TerminalPixelPosition(0, -1) }
        val position = TerminalMousePosition(TerminalCellPosition(0, 0))

        assertFailsWith<IllegalArgumentException> {
            TerminalMouseInput.Press(
                TerminalMouseButton.RIGHT,
                position,
                TerminalMouseButtons(left = true),
            )
        }
        assertFailsWith<IllegalArgumentException> {
            TerminalMouseInput.Release(
                TerminalMouseButton.LEFT,
                position,
                TerminalMouseButtons(left = true),
            )
        }
    }
}

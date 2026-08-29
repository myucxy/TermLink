package dev.termlink.terminal.api

/** A zero-based cell coordinate in the active terminal viewport. */
data class TerminalCellPosition(
    val column: Int,
    val row: Int,
) {
    init {
        require(column >= 0) { "cell column must be non-negative" }
        require(row >= 0) { "cell row must be non-negative" }
    }
}

/** A zero-based pixel coordinate relative to the active terminal viewport. */
data class TerminalPixelPosition(
    val x: Int,
    val y: Int,
) {
    init {
        require(x >= 0) { "pixel x must be non-negative" }
        require(y >= 0) { "pixel y must be non-negative" }
    }
}

data class TerminalMousePosition(
    val cell: TerminalCellPosition,
    val pixel: TerminalPixelPosition? = null,
)

enum class TerminalMouseButton { LEFT, MIDDLE, RIGHT }

enum class TerminalMouseWheelDirection { UP, DOWN, LEFT, RIGHT }

/** Physical primary-button state after the event. */
data class TerminalMouseButtons(
    val left: Boolean = false,
    val middle: Boolean = false,
    val right: Boolean = false,
) {
    fun contains(button: TerminalMouseButton): Boolean = when (button) {
        TerminalMouseButton.LEFT -> left
        TerminalMouseButton.MIDDLE -> middle
        TerminalMouseButton.RIGHT -> right
    }
}

/**
 * Semantic mouse input. The sealed action types prevent unused button/wheel combinations.
 *
 * [buttonsHeld] describes physical state after this input: a press includes its button and a
 * release excludes it. Coordinates use a zero-based viewport origin. [TerminalPixelPosition] is
 * optional because only pixel-reporting mode 1016 requires it.
 */
sealed interface TerminalMouseInput {
    val position: TerminalMousePosition
    val buttonsHeld: TerminalMouseButtons
    val modifiers: TerminalInputModifiers

    data class Press(
        val button: TerminalMouseButton,
        override val position: TerminalMousePosition,
        override val buttonsHeld: TerminalMouseButtons,
        override val modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ) : TerminalMouseInput {
        init {
            require(buttonsHeld.contains(button)) {
                "buttonsHeld must include the pressed button"
            }
        }
    }

    data class Release(
        val button: TerminalMouseButton,
        override val position: TerminalMousePosition,
        override val buttonsHeld: TerminalMouseButtons = TerminalMouseButtons(),
        override val modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ) : TerminalMouseInput {
        init {
            require(!buttonsHeld.contains(button)) {
                "buttonsHeld must exclude the released button"
            }
        }
    }

    data class Move(
        override val position: TerminalMousePosition,
        override val buttonsHeld: TerminalMouseButtons = TerminalMouseButtons(),
        override val modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ) : TerminalMouseInput

    data class Wheel(
        val direction: TerminalMouseWheelDirection,
        override val position: TerminalMousePosition,
        override val buttonsHeld: TerminalMouseButtons = TerminalMouseButtons(),
        override val modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ) : TerminalMouseInput
}

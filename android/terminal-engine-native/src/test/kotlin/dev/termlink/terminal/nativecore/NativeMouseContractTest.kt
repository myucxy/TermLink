package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalCellPosition
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalMouseButton
import dev.termlink.terminal.api.TerminalMouseButtons
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMousePosition
import dev.termlink.terminal.api.TerminalMouseWheelDirection
import dev.termlink.terminal.api.TerminalPixelPosition
import dev.termlink.terminal.api.TerminalSize
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertTrue

class NativeMouseContractTest {
    @Test
    fun semanticActionsMapExactlyToTheFixed64ByteAbi() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)
        terminal.feed(1, "\u001b[?1003h\u001b[?1006h".encodeToByteArray())
        val position = TerminalMousePosition(
            cell = TerminalCellPosition(3, 1),
            pixel = TerminalPixelPosition(49, 29),
        )

        assertContentEquals(
            "\u001b[<28;4;2M".encodeToByteArray(),
            terminal.encodeMouse(
                TerminalMouseInput.Press(
                    button = TerminalMouseButton.LEFT,
                    position = position,
                    buttonsHeld = TerminalMouseButtons(left = true, right = true),
                    modifiers = TerminalInputModifiers(
                        control = true,
                        alt = true,
                        shift = true,
                        meta = true,
                    ),
                ),
            ),
        )
        assertEquals(
            NativeMouseInputV1(
                action = NATIVE_MOUSE_ACTION_PRESS,
                button = NATIVE_MOUSE_BUTTON_LEFT,
                wheel = NATIVE_MOUSE_WHEEL_NONE,
                buttonsHeld = NATIVE_MOUSE_BUTTON_HELD_LEFT or
                    NATIVE_MOUSE_BUTTON_HELD_RIGHT,
                positionFlags = NATIVE_MOUSE_POSITION_PIXEL,
                modifiers = NATIVE_INPUT_MODIFIER_SHIFT or NATIVE_INPUT_MODIFIER_ALT or
                    NATIVE_INPUT_MODIFIER_CONTROL or NATIVE_INPUT_MODIFIER_META,
                cellColumn = 3,
                cellRow = 1,
                pixelX = 49,
                pixelY = 29,
            ),
            bridge.lastMouseInput,
        )
        assertEquals(TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE, bridge.lastMouseInput?.structSize)

        assertContentEquals(
            "\u001b[<2;4;2m".encodeToByteArray(),
            terminal.encodeMouse(
                TerminalMouseInput.Release(
                    TerminalMouseButton.RIGHT,
                    position,
                    TerminalMouseButtons(left = true),
                ),
            ),
        )
        assertContentEquals(
            "\u001b[<33;4;2M".encodeToByteArray(),
            terminal.encodeMouse(
                TerminalMouseInput.Move(
                    position,
                    TerminalMouseButtons(middle = true),
                ),
            ),
        )
        assertContentEquals(
            "\u001b[<65;4;2M".encodeToByteArray(),
            terminal.encodeMouse(
                TerminalMouseInput.Wheel(
                    TerminalMouseWheelDirection.DOWN,
                    position,
                ),
            ),
        )
        assertEquals(2L, terminal.nextSourceSequence, "encoding must not consume journal input")
        assertEquals(2L, terminal.presentationRevision)
        terminal.close()
    }

    @Test
    fun noTrackingIsAnOwnedEmptySuccessAnd1016RequiresPixels() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)
        val cellOnly = TerminalMousePosition(TerminalCellPosition(3, 1))
        val pressWithoutPixels = TerminalMouseInput.Press(
            TerminalMouseButton.LEFT,
            cellOnly,
            TerminalMouseButtons(left = true),
        )

        assertContentEquals(byteArrayOf(), terminal.encodeMouse(pressWithoutPixels))
        terminal.feed(1, "\u001b[?1000h\u001b[?1016h".encodeToByteArray())

        val missingPixels = assertFailsWith<NativeTerminalException> {
            terminal.encodeMouse(pressWithoutPixels)
        }
        assertEquals(NativeTerminalStatus.INVALID_ARGUMENT, missingPixels.status)
        assertEquals(2L, terminal.nextSourceSequence)

        val withPixels = TerminalMouseInput.Press(
            TerminalMouseButton.LEFT,
            TerminalMousePosition(
                cell = TerminalCellPosition(3, 1),
                pixel = TerminalPixelPosition(49, 29),
            ),
            TerminalMouseButtons(left = true),
        )
        assertContentEquals(
            "\u001b[<0;50;30M".encodeToByteArray(),
            terminal.encodeMouse(withPixels),
        )

        val outside = assertFailsWith<NativeTerminalException> {
            terminal.encodeMouse(
                TerminalMouseInput.Move(
                    TerminalMousePosition(TerminalCellPosition(80, 0)),
                ),
            )
        }
        assertEquals(NativeTerminalStatus.INVALID_ARGUMENT, outside.status)
        assertContentEquals(
            "\u001b[<0;50;30M".encodeToByteArray(),
            terminal.encodeMouse(withPixels),
            "invalid input must not poison a live handle",
        )
        terminal.close()
    }

    @Test
    fun fakeValidatesRawAbiBeforeEncodingAndRecoversAfterRejection() {
        val bridge = FakeNativeBridge()
        val allocation = assertIs<NativeBridgeResult.Success<NativeBridgeTerminal>>(
            bridge.create(TerminalSize(8, 4), 0),
        ).value
        val valid = NativeMouseInputV1(
            action = NATIVE_MOUSE_ACTION_PRESS,
            button = NATIVE_MOUSE_BUTTON_LEFT,
            wheel = NATIVE_MOUSE_WHEEL_NONE,
            buttonsHeld = NATIVE_MOUSE_BUTTON_HELD_LEFT,
            positionFlags = 0,
            modifiers = 0,
            cellColumn = 1,
            cellRow = 1,
            pixelX = 0,
            pixelY = 0,
        )
        val invalid = listOf(
            valid.copy(abiVersion = 2),
            valid.copy(structSize = 63),
            valid.copy(action = NATIVE_MOUSE_ACTION_MOVE),
            valid.copy(wheel = NATIVE_MOUSE_WHEEL_UP),
            valid.copy(buttonsHeld = 0),
            valid.copy(positionFlags = 2),
            valid.copy(pixelX = 1),
            valid.copy(modifiers = 16),
            valid.copy(cellColumn = 8),
            valid.copy(reserved1 = 1),
        )

        invalid.forEach { input ->
            val failure = assertIs<NativeBridgeResult.Failure>(
                bridge.encodeMouse(allocation.handle, input),
            )
            assertEquals(NativeTerminalStatus.INVALID_ARGUMENT.code, failure.statusCode)
        }
        val recovered = assertIs<NativeBridgeResult.Success<NativeBridgeEncodedInput>>(
            bridge.encodeMouse(allocation.handle, valid),
        )
        assertContentEquals(byteArrayOf(), recovered.value.payload)
        assertIs<NativeBridgeResult.Success<Unit>>(bridge.destroy(allocation.handle))
    }

    @Test
    fun splitModeSequencesAndCheckpointRestorePreserveMouseSemantics() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 4), 0)
        terminal.feedBatch(
            0,
            listOf(
                "\u001b[?1000h\u001b[?10".encodeToByteArray(),
                "06h".encodeToByteArray(),
            ),
        )
        val press = TerminalMouseInput.Press(
            TerminalMouseButton.MIDDLE,
            TerminalMousePosition(TerminalCellPosition(1, 2)),
            TerminalMouseButtons(middle = true),
        )
        assertContentEquals("\u001b[<1;2;3M".encodeToByteArray(), terminal.encodeMouse(press))

        val checkpoint = terminal.exportCheckpoint()
        val restored = NativeTerminalHandle.importCheckpoint(bridge, checkpoint.copyPayload())
        assertContentEquals("\u001b[<1;2;3M".encodeToByteArray(), restored.encodeMouse(press))
        terminal.close()
        restored.close()
    }

    @Test
    fun bridgeFailuresAndReturnedBuffersHaveExplicitRecoveryBoundaries() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 4), 0)
        val move = TerminalMouseInput.Move(
            TerminalMousePosition(TerminalCellPosition(0, 0)),
        )
        bridge.forcedMouseFailure = NativeBridgeResult.Failure(
            NativeTerminalStatus.INVALID_ARGUMENT.code,
        )
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT,
            assertFailsWith<NativeTerminalException> { terminal.encodeMouse(move) }.status,
        )
        bridge.forcedMouseFailure = null
        assertContentEquals(byteArrayOf(), terminal.encodeMouse(move))

        val raw = "owned".encodeToByteArray()
        bridge.forcedMouseEncodedInput = NativeBridgeEncodedInput(raw)
        val detached = terminal.encodeMouse(move)
        raw.fill(0)
        assertContentEquals("owned".encodeToByteArray(), detached)
        bridge.forcedMouseEncodedInput = null

        terminal.close()
        assertFailsWith<IllegalStateException> { terminal.encodeMouse(move) }
        assertTrue(bridge.mouseCalls >= 3)
    }
}

package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertTrue

class NativeWin32KeyContractTest {
    @Test
    fun abiDiscoveryMismatchFailsBeforeAllocatingAHandle() {
        val bridge = FakeNativeBridge().apply { inputV2StructSize -= 1 }

        val failure = assertFailsWith<NativeTerminalException> {
            NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 0)
        }

        assertEquals(NativeTerminalStatus.ABI_MISMATCH, failure.status)
        assertEquals("inputV2StructSize", failure.operation)
        assertEquals(0, bridge.destroyCalls)
    }

    @Test
    fun handleBuildsTheCanonicalV2RecordWithDefaultCompatibilityFields() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 0)
        terminal.feed(0, "\u001b[?9001h".encodeToByteArray())

        assertContentEquals(
            "\u001b[112;59;55296;1;8;1_".encodeToByteArray(),
            terminal.encodeWin32Key(112, 59, 0xd800, true, 8, 1),
        )
        assertEquals(
            NativeWin32KeyInputV2(
                win32VirtualKey = 112,
                win32ScanCode = 59,
                win32UnicodeChar = 0xd800,
                win32KeyDown = 1,
                win32ControlKeyState = 8,
                win32RepeatCount = 1,
            ),
            bridge.lastInputV2,
        )
        assertEquals(TERMLINK_TERMINAL_ABI_VERSION, bridge.lastInputV2?.abiVersion)
        assertEquals(TERMLINK_INPUT_V2_STRUCT_SIZE, bridge.lastInputV2?.structSize)
        assertEquals(NATIVE_INPUT_KIND_WIN32_KEY, bridge.lastInputV2?.kind)
        assertEquals(0, bridge.lastInputV2?.key)
        assertEquals(0, bridge.lastInputV2?.modifiers)
        assertEquals(0, bridge.lastInputV2?.character)
        assertEquals(0L, bridge.lastInputV2?.data)
        assertEquals(0L, bridge.lastInputV2?.dataLength)
        assertEquals(0L, bridge.lastInputV2?.reservedV1Compat0)
        assertEquals(0L, bridge.lastInputV2?.reservedV1Compat1)
        assertEquals(0L, bridge.lastInputV2?.reserved0)
        assertEquals(0L, bridge.lastInputV2?.reserved1)
        assertEquals(1L, terminal.nextSourceSequence, "encoding must not consume journal input")
        terminal.close()
    }

    @Test
    fun disabledModeIsRetryableUnsupportedThenCtrlF1VectorsEncodeExactly() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 0)

        val disabled = assertFailsWith<NativeTerminalException> {
            terminal.encodeWin32Key(17, 29, 0, true, 8, 1)
        }
        assertEquals(NativeTerminalStatus.UNSUPPORTED, disabled.status)
        assertEquals(0L, terminal.nextSourceSequence, "unsupported must not poison the handle")

        terminal.feed(0, "\u001b[?9001h".encodeToByteArray())
        val vectors = listOf(
            Win32Vector(17, 29, 8, "\u001b[17;29;0;1;8;1_", keyDown = true),
            Win32Vector(112, 59, 8, "\u001b[112;59;0;1;8;1_", keyDown = true),
            Win32Vector(112, 59, 8, "\u001b[112;59;0;0;8;1_", keyDown = false),
            Win32Vector(17, 29, 0, "\u001b[17;29;0;0;0;1_", keyDown = false),
        )
        vectors.forEach { vector ->
            assertContentEquals(
                vector.expected.encodeToByteArray(),
                terminal.encodeWin32Key(
                    vector.virtualKey,
                    vector.scanCode,
                    0,
                    vector.keyDown,
                    vector.controlKeyState,
                    1,
                ),
            )
        }
        assertEquals(5, bridge.inputV2Calls)
        terminal.close()
    }

    @Test
    fun zeroMaximumAndUtf16CodeUnitValuesArePreserved() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 0)
        terminal.feed(0, "\u001b[?9001h".encodeToByteArray())

        assertContentEquals(
            "\u001b[0;0;0;0;0;0_".encodeToByteArray(),
            terminal.encodeWin32Key(0, 0, 0, false, 0, 0),
        )
        assertContentEquals(
            "\u001b[65535;65535;65535;1;4294967295;65535_".encodeToByteArray(),
            terminal.encodeWin32Key(
                0xffff,
                0xffff,
                0xffff,
                true,
                0xffff_ffffL,
                0xffff,
            ),
        )
        // An unpaired surrogate is still one valid UTF-16 code unit for this protocol.
        assertContentEquals(
            "\u001b[0;0;56319;0;0;0_".encodeToByteArray(),
            terminal.encodeWin32Key(0, 0, 0xdbff, false, 0, 0),
        )
        terminal.close()
    }

    @Test
    fun semanticRangeFailuresStayLocalAndRawBridgeValidationCoversTheWholeAbi() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 0)

        val invalidCalls = listOf<() -> Unit>(
            { terminal.encodeWin32Key(-1, 0, 0, false, 0, 0) },
            { terminal.encodeWin32Key(0x1_0000, 0, 0, false, 0, 0) },
            { terminal.encodeWin32Key(0, -1, 0, false, 0, 0) },
            { terminal.encodeWin32Key(0, 0, -1, false, 0, 0) },
            { terminal.encodeWin32Key(0, 0, 0x1_0000, false, 0, 0) },
            { terminal.encodeWin32Key(0, 0, 0, false, -1, 0) },
            { terminal.encodeWin32Key(0, 0, 0, false, 0x1_0000_0000L, 0) },
            { terminal.encodeWin32Key(0, 0, 0, false, 0, -1) },
            { terminal.encodeWin32Key(0, 0, 0, false, 0, 0x1_0000) },
        )
        invalidCalls.forEach { call -> assertFailsWith<IllegalArgumentException> { call() } }
        assertEquals(0, bridge.inputV2Calls)
        terminal.close()

        val rawBridge = FakeNativeBridge()
        val allocation = assertIs<NativeBridgeResult.Success<NativeBridgeTerminal>>(
            rawBridge.create(TerminalSize(8, 2), 0),
        ).value
        assertIs<NativeBridgeResult.Success<NativeBridgeApplyReceipt>>(
            rawBridge.applyBatch(
                allocation.handle,
                NativeEventBatch.of(
                    listOf(NativeSourceEvent.Output(0, "\u001b[?9001h".encodeToByteArray())),
                ),
            ),
        )
        val valid = NativeWin32KeyInputV2(
            win32VirtualKey = 112,
            win32ScanCode = 59,
            win32UnicodeChar = 0,
            win32KeyDown = 1,
            win32ControlKeyState = 8,
            win32RepeatCount = 1,
        )
        val malformed = listOf(
            valid.copy(abiVersion = 2),
            valid.copy(structSize = TERMLINK_INPUT_V2_STRUCT_SIZE - 1),
            valid.copy(kind = 4),
            valid.copy(key = 1),
            valid.copy(modifiers = 1),
            valid.copy(character = 1),
            valid.copy(data = 1),
            valid.copy(dataLength = 1),
            valid.copy(reservedV1Compat0 = 1),
            valid.copy(reservedV1Compat1 = 1),
            valid.copy(win32VirtualKey = -1),
            valid.copy(win32VirtualKey = 0x1_0000),
            valid.copy(win32ScanCode = 0x1_0000),
            valid.copy(win32UnicodeChar = 0x1_0000),
            valid.copy(win32KeyDown = -1),
            valid.copy(win32KeyDown = 2),
            valid.copy(win32ControlKeyState = -1),
            valid.copy(win32ControlKeyState = 0x1_0000_0000L),
            valid.copy(win32RepeatCount = -1),
            valid.copy(win32RepeatCount = 0x1_0000),
            valid.copy(reserved0 = 1),
            valid.copy(reserved1 = 1),
        )
        malformed.forEach { input ->
            val failure = assertIs<NativeBridgeResult.Failure>(
                rawBridge.encodeInputV2(allocation.handle, input),
            )
            assertEquals(NativeTerminalStatus.INVALID_ARGUMENT.code, failure.statusCode)
        }
        val forwardCompatible = assertIs<NativeBridgeResult.Success<NativeBridgeEncodedInput>>(
            rawBridge.encodeInputV2(
                allocation.handle,
                valid.copy(structSize = TERMLINK_INPUT_V2_STRUCT_SIZE + 8),
            ),
        )
        assertContentEquals("\u001b[112;59;0;1;8;1_".encodeToByteArray(), forwardCompatible.value.payload)
        assertIs<NativeBridgeResult.Success<Unit>>(rawBridge.destroy(allocation.handle))
    }

    @Test
    fun splitModeParserDisableAndCheckpointRestorePreserveModeSemantics() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 0)
        terminal.feedBatch(
            0,
            listOf(
                "\u001b[?90".encodeToByteArray(),
                "01h".encodeToByteArray(),
            ),
        )
        assertTrue(bridge.onlyStateForTest().win32InputMode)
        val checkpoint = terminal.exportCheckpoint()
        val restored = NativeTerminalHandle.importCheckpoint(bridge, checkpoint.copyPayload())

        assertContentEquals(
            "\u001b[112;59;0;1;8;1_".encodeToByteArray(),
            restored.encodeWin32Key(112, 59, 0, true, 8, 1),
        )
        restored.feed(2, "\u001b[?9001l".encodeToByteArray())
        assertEquals(
            NativeTerminalStatus.UNSUPPORTED,
            assertFailsWith<NativeTerminalException> {
                restored.encodeWin32Key(112, 59, 0, true, 8, 1)
            }.status,
        )
        restored.feed(3, "\u001b[?9001h".encodeToByteArray())
        assertContentEquals(
            "\u001b[112;59;0;1;8;1_".encodeToByteArray(),
            restored.encodeWin32Key(112, 59, 0, true, 8, 1),
        )
        terminal.close()
        restored.close()
    }

    @Test
    fun retryableFailuresAndReturnedBuffersMatchTheExistingInputBoundary() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 0)
        terminal.feed(0, "\u001b[?9001h".encodeToByteArray())
        bridge.forcedInputV2Failure = NativeBridgeResult.Failure(
            NativeTerminalStatus.INVALID_ARGUMENT.code,
        )

        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT,
            assertFailsWith<NativeTerminalException> {
                terminal.encodeWin32Key(0, 0, 0, false, 0, 0)
            }.status,
        )
        bridge.forcedInputV2Failure = null
        assertContentEquals(
            "\u001b[0;0;0;0;0;0_".encodeToByteArray(),
            terminal.encodeWin32Key(0, 0, 0, false, 0, 0),
        )

        bridge.throwOnInputV2 = IllegalStateException("fixture bridge failed")
        assertEquals(
            NativeTerminalStatus.BRIDGE_FAILURE,
            assertFailsWith<NativeTerminalException> {
                terminal.encodeWin32Key(0, 0, 0, false, 0, 0)
            }.status,
        )
        bridge.throwOnInputV2 = null

        val raw = "owned".encodeToByteArray()
        bridge.forcedInputV2EncodedInput = NativeBridgeEncodedInput(raw)
        val detached = terminal.encodeWin32Key(0, 0, 0, false, 0, 0)
        raw.fill(0)
        assertContentEquals("owned".encodeToByteArray(), detached)
        terminal.close()
    }

    @Test
    fun failedClosedPoisonsWhileCloseRejectsWithoutEnteringTheBridge() {
        val bridge = FakeNativeBridge().apply {
            forcedInputV2Failure = NativeBridgeResult.Failure(
                NativeTerminalStatus.FAILED_CLOSED.code,
            )
        }
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 0)

        assertEquals(
            NativeTerminalStatus.FAILED_CLOSED,
            assertFailsWith<NativeTerminalException> {
                terminal.encodeWin32Key(0, 0, 0, false, 0, 0)
            }.status,
        )
        bridge.forcedInputV2Failure = null
        assertFailsWith<IllegalStateException> {
            terminal.encodeWin32Key(0, 0, 0, false, 0, 0)
        }
        assertEquals(1, bridge.inputV2Calls)

        terminal.close()
        terminal.close()
        assertEquals(1, bridge.destroyCalls)
        assertFailsWith<IllegalStateException> {
            terminal.encodeWin32Key(0, 0, 0, false, 0, 0)
        }
        assertEquals(1, bridge.inputV2Calls)
    }

    private data class Win32Vector(
        val virtualKey: Int,
        val scanCode: Int,
        val controlKeyState: Long,
        val expected: String,
        val keyDown: Boolean,
    )
}

package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalKittyFunctionalKey
import dev.termlink.terminal.api.TerminalKittyKey
import dev.termlink.terminal.api.TerminalKittyKeyEventType
import dev.termlink.terminal.api.TerminalKittyKeyInput
import dev.termlink.terminal.api.TerminalKittyModifiers
import dev.termlink.terminal.api.TerminalSize
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs
import kotlin.test.assertTrue

class NativeInputV3HostEffectV2BridgeTest {
    @Test
    fun rejectsInputV3AndHostEffectV2DiscoveryMismatchesBeforeAllocation() {
        val bridges = listOf(
            FakeNativeBridge().apply { inputV3StructSize -= 1 },
            FakeNativeBridge().apply { hostEffectFrameV2FormatVersion += 1 },
            FakeNativeBridge().apply { maxHostEffectFrameV2Bytes -= 1 },
        )

        bridges.forEach { bridge ->
            val failure = assertFailsWith<NativeTerminalException> {
                NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)
            }

            assertEquals(NativeTerminalStatus.ABI_MISMATCH, failure.status)
            assertEquals(0, bridge.destroyCalls)
        }
    }

    @Test
    fun semanticKittyInputProducesTheExactOwnedV3Contract() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)
        val bridgeOutput = byteArrayOf(7, 8, 9)
        bridge.forcedInputV3EncodedInput = NativeBridgeEncodedInput(bridgeOutput)
        val associatedText = "é🙂".encodeToByteArray()

        val encoded = terminal.encodeKittyKey(
            TerminalKittyKeyInput(
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
            ),
        )

        val captured = assertIs<NativeKittyKeyInputV3>(bridge.lastInputV3)
        assertEquals(TERMLINK_TERMINAL_ABI_VERSION, captured.abiVersion)
        assertEquals(160, captured.structSize)
        assertEquals(NATIVE_INPUT_KIND_KITTY_KEY, captured.kind)
        assertEquals(NATIVE_INPUT_KEY_CHARACTER, captured.key)
        assertEquals(0, captured.modifiers)
        assertEquals('a'.code, captured.character)
        assertEquals(0L, captured.data)
        assertEquals(0L, captured.dataLength)
        assertEquals(0L, captured.reservedV1Compat0)
        assertEquals(0L, captured.reservedV1Compat1)
        assertEquals(0, captured.win32VirtualKey)
        assertEquals(0, captured.win32ScanCode)
        assertEquals(0, captured.win32UnicodeChar)
        assertEquals(0, captured.win32KeyDown)
        assertEquals(0L, captured.win32ControlKeyState)
        assertEquals(0, captured.win32RepeatCount)
        assertEquals(0L, captured.reservedV2Prefix0)
        assertEquals(0L, captured.reservedV2Prefix1)
        assertEquals(NATIVE_KITTY_EVENT_REPEAT, captured.kittyEventType)
        assertEquals(0xff, captured.kittyModifiers)
        assertEquals('A'.code, captured.kittyShiftedKey)
        assertEquals('q'.code, captured.kittyBaseLayoutKey)
        assertEquals(associatedText.size.toLong(), captured.associatedTextLength)
        assertContentEquals(associatedText, captured.copyAssociatedText())
        assertEquals(0L, captured.reservedV3Tail0)
        assertEquals(0L, captured.reservedV3Tail1)
        assertEquals(0L, captured.reservedV3Tail2)
        assertEquals(0L, captured.reservedV3Tail3)

        bridgeOutput.fill(0)
        assertContentEquals(byteArrayOf(7, 8, 9), encoded)
        val detachedText = captured.copyAssociatedText()
        detachedText.fill(0)
        assertContentEquals(associatedText, captured.copyAssociatedText())
        terminal.close()
    }

    @Test
    fun characterCommonPuaAndUnidentifiedIdentitiesUseCanonicalValues() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)

        fun capture(input: TerminalKittyKeyInput): NativeKittyKeyInputV3 {
            terminal.encodeKittyKey(input)
            return assertIs<NativeKittyKeyInputV3>(bridge.lastInputV3)
        }

        val character = capture(TerminalKittyKeyInput(TerminalKittyKey.Character('x'.code)))
        assertEquals(1L, character.key)
        assertEquals('x'.code, character.character)

        val common = capture(
            TerminalKittyKeyInput(
                TerminalKittyKey.Functional(TerminalKittyFunctionalKey.F3),
            ),
        )
        assertEquals(18L, common.key)
        assertEquals(0, common.character)

        val commonKeypad = capture(
            TerminalKittyKeyInput(
                TerminalKittyKey.Functional(TerminalKittyFunctionalKey.KEYPAD_ENTER),
            ),
        )
        assertEquals(117L, commonKeypad.key)

        val firstPua = capture(
            TerminalKittyKeyInput(
                TerminalKittyKey.Functional(TerminalKittyFunctionalKey.CAPS_LOCK),
            ),
        )
        assertEquals(57_358L, firstPua.key)

        val extendedFunction = capture(
            TerminalKittyKeyInput(
                TerminalKittyKey.Functional(TerminalKittyFunctionalKey.F13),
            ),
        )
        assertEquals(57_376L, extendedFunction.key)

        val lastPua = capture(
            TerminalKittyKeyInput(
                TerminalKittyKey.Functional(TerminalKittyFunctionalKey.ISO_LEVEL5_SHIFT),
            ),
        )
        assertEquals(57_454L, lastPua.key)

        val unidentified = capture(
            TerminalKittyKeyInput(
                key = TerminalKittyKey.UnidentifiedText,
                associatedText = "input-method text",
            ),
        )
        assertEquals(0xffff_ffffL, unidentified.key)
        assertEquals(0, unidentified.character)
        terminal.close()
    }

    @Test
    fun fakeBridgeRejectsMalformedRawV3DtosWithoutEncoding() {
        val bridge = FakeNativeBridge()
        val allocation = assertIs<NativeBridgeResult.Success<NativeBridgeTerminal>>(
            bridge.create(TerminalSize(80, 24), 1),
        ).value
        val ownedAssociatedText = "owned".encodeToByteArray()
        val ownedInput = rawKittyInput(associatedText = ownedAssociatedText)
        ownedAssociatedText.fill(0)
        assertContentEquals("owned".encodeToByteArray(), ownedInput.copyAssociatedText())
        val malformed = listOf(
            rawKittyInput(abiVersion = 2),
            rawKittyInput(structSize = TERMLINK_INPUT_V3_STRUCT_SIZE - 1),
            rawKittyInput(kind = NATIVE_INPUT_KIND_WIN32_KEY),
            rawKittyInput(key = 0x1_0000_0000L),
            rawKittyInput(modifiers = 1),
            rawKittyInput(data = 1),
            rawKittyInput(reservedV1Compat0 = 1),
            rawKittyInput(win32VirtualKey = 1),
            rawKittyInput(reservedV2Prefix0 = 1),
            rawKittyInput(kittyEventType = 0),
            rawKittyInput(kittyModifiers = 0x100),
            rawKittyInput(character = 0),
            rawKittyInput(character = 0x80),
            rawKittyInput(kittyShiftedKey = 'A'.code),
            rawKittyInput(
                key = 2,
                character = 0,
                kittyModifiers = NATIVE_KITTY_MODIFIER_SHIFT,
                kittyShiftedKey = 'A'.code,
            ),
            rawKittyInput(
                key = NATIVE_INPUT_KITTY_KEY_UNIDENTIFIED_TEXT,
                character = 0,
            ),
            rawKittyInput(
                key = NATIVE_INPUT_KITTY_KEY_UNIDENTIFIED_TEXT,
                character = 0,
                kittyEventType = NATIVE_KITTY_EVENT_REPEAT,
                associatedText = "text".encodeToByteArray(),
            ),
            rawKittyInput(
                kittyEventType = NATIVE_KITTY_EVENT_RELEASE,
                associatedText = "text".encodeToByteArray(),
            ),
            rawKittyInput(associatedText = byteArrayOf(0xc3.toByte())),
            rawKittyInput(
                associatedText = "text".encodeToByteArray(),
                associatedTextLength = 3,
            ),
            rawKittyInput(associatedText = ByteArray(4_097) { 'a'.code.toByte() }),
            rawKittyInput(associatedText = "a".repeat(1_025).encodeToByteArray()),
            rawKittyInput(reservedV3Tail0 = 1),
        )

        malformed.forEachIndexed { index, input ->
            val failure = assertIs<NativeBridgeResult.Failure>(
                bridge.encodeInputV3(allocation.handle, input),
                "malformed DTO at index $index was accepted",
            )
            assertEquals(NativeTerminalStatus.INVALID_ARGUMENT.code, failure.statusCode)
        }
        assertEquals(malformed.size, bridge.inputV3Calls)
        assertIs<NativeBridgeResult.Success<Unit>>(bridge.destroy(allocation.handle))
    }

    @Test
    fun status14LeavesTheSequenceRetryableThroughTheV2EntryPoint() {
        val bridge = FakeNativeBridge().apply {
            forcedApplyFailure = NativeBridgeResult.Failure(
                NativeTerminalStatus.HOST_EFFECT_VERSION_REQUIRED.code,
            )
        }
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)

        val required = assertFailsWith<NativeTerminalException> {
            terminal.feed(1, "query".encodeToByteArray())
        }

        assertEquals(NativeTerminalStatus.HOST_EFFECT_VERSION_REQUIRED, required.status)
        assertEquals(1L, terminal.nextSourceSequence)
        assertEquals(1L, bridge.onlyStateForTest().nextSourceSequence)
        bridge.forcedApplyFailure = null
        val frame = terminal.feedWithHostEffectsV2(1, "query".encodeToByteArray())
        assertTrue(frame.effects.isEmpty())
        assertEquals(2L, terminal.nextSourceSequence)
        assertEquals(2L, bridge.onlyStateForTest().nextSourceSequence)
        terminal.close()
    }

    @Test
    fun v2FramePreservesSameSourceKeyOrdinalsAndDetachesAllBuffers() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)
        val firstReply = "\u001b[?1;2c".encodeToByteArray()
        val secondReply = "\u001b[0n".encodeToByteArray()
        val rawFrame = fakeHostEffectFrameV2(
            listOf(
                FakeHostEffectV2Record(1, 3, 0, firstReply),
                FakeHostEffectV2Record(1, 3, 1, secondReply),
            ),
        )
        val expectedRawFrame = rawFrame.copyOf()
        bridge.forcedHostEffectV2Result = NativeBridgeApplyWithHostEffectsV2(
            receipt = NativeBridgeApplyReceipt(3, 3),
            hostEffectFrame = rawFrame,
        )
        rawFrame.fill(0)
        val firstChunk = byteArrayOf(1, 2, 3)
        val secondChunk = byteArrayOf(4, 5)

        val frame = terminal.feedBatchWithHostEffectsV2(1, listOf(firstChunk, secondChunk))

        firstChunk.fill(9)
        secondChunk.fill(9)
        assertContentEquals(expectedRawFrame, frame.copyRawFrame())
        assertEquals(2, frame.effects.size)
        assertEquals(0L, frame.effects[0].ordinal)
        assertEquals(1L, frame.effects[1].ordinal)
        assertEquals(frame.effects[0].sourceSequence, frame.effects[1].sourceSequence)
        assertEquals(frame.effects[0].sourceOffsetEnd, frame.effects[1].sourceOffsetEnd)
        assertContentEquals(firstReply, frame.effects[0].copyPayload())
        assertContentEquals(secondReply, frame.effects[1].copyPayload())
        val detachedReply = frame.effects[0].copyPayload()
        detachedReply.fill(0)
        assertContentEquals(firstReply, frame.effects[0].copyPayload())

        val captured = assertIs<NativeEventBatch>(bridge.lastHostEffectV2Batch)
        val capturedOutputs = captured.events.map { assertIs<NativeSourceEvent.Output>(it) }
        assertContentEquals(byteArrayOf(1, 2, 3), capturedOutputs[0].copyBytes())
        assertContentEquals(byteArrayOf(4, 5), capturedOutputs[1].copyBytes())
        assertEquals(3L, terminal.nextSourceSequence)
        terminal.close()
    }

    @Test
    fun malformedSuccessfulV2ReceiptsAndFramesPoisonTheirHandles() {
        val receiptBridge = FakeNativeBridge()
        val receiptTerminal = NativeTerminalHandle.create(
            receiptBridge,
            TerminalSize(80, 24),
            1,
        )
        receiptBridge.forcedHostEffectV2Result = NativeBridgeApplyWithHostEffectsV2(
            receipt = NativeBridgeApplyReceipt(99, 99),
            hostEffectFrame = fakeHostEffectFrameV2(),
        )

        assertFailsWith<NativeTerminalContractException> {
            receiptTerminal.feedWithHostEffectsV2(1, byteArrayOf(1))
        }
        assertFailsWith<IllegalStateException> { receiptTerminal.render() }
        receiptTerminal.close()

        val frameBridge = FakeNativeBridge()
        val frameTerminal = NativeTerminalHandle.create(frameBridge, TerminalSize(80, 24), 1)
        frameBridge.forcedHostEffectV2Result = NativeBridgeApplyWithHostEffectsV2(
            receipt = NativeBridgeApplyReceipt(2, 2),
            hostEffectFrame = ByteArray(64),
        )

        val frameFailure = assertFailsWith<NativeTerminalContractException> {
            frameTerminal.feedWithHostEffectsV2(1, byteArrayOf(1))
        }
        assertEquals("applyBatchWithHostEffectsV2.frameDecode", frameFailure.terminalOperation)
        assertEquals("MALFORMED_BRIDGE_RESULT", frameFailure.terminalStatusName)
        assertEquals(257, frameFailure.terminalStatusCode)
        assertFailsWith<IllegalStateException> { frameTerminal.render() }
        frameTerminal.close()
    }
}

private fun rawKittyInput(
    abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION,
    structSize: Int = TERMLINK_INPUT_V3_STRUCT_SIZE,
    kind: Int = NATIVE_INPUT_KIND_KITTY_KEY,
    key: Long = NATIVE_INPUT_KEY_CHARACTER,
    modifiers: Int = 0,
    character: Int = 'a'.code,
    data: Long = 0,
    reservedV1Compat0: Long = 0,
    win32VirtualKey: Int = 0,
    reservedV2Prefix0: Long = 0,
    kittyEventType: Int = NATIVE_KITTY_EVENT_PRESS,
    kittyModifiers: Int = 0,
    kittyShiftedKey: Int = 0,
    associatedText: ByteArray = byteArrayOf(),
    associatedTextLength: Long = associatedText.size.toLong(),
    reservedV3Tail0: Long = 0,
): NativeKittyKeyInputV3 = NativeKittyKeyInputV3(
    abiVersion = abiVersion,
    structSize = structSize,
    kind = kind,
    key = key,
    modifiers = modifiers,
    character = character,
    data = data,
    reservedV1Compat0 = reservedV1Compat0,
    win32VirtualKey = win32VirtualKey,
    reservedV2Prefix0 = reservedV2Prefix0,
    kittyEventType = kittyEventType,
    kittyModifiers = kittyModifiers,
    kittyShiftedKey = kittyShiftedKey,
    kittyBaseLayoutKey = 0,
    associatedText = associatedText,
    associatedTextLength = associatedTextLength,
    reservedV3Tail0 = reservedV3Tail0,
)

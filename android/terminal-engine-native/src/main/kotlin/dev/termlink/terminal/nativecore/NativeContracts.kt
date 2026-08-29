package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalKittyFunctionalKey
import dev.termlink.terminal.api.TerminalKittyKey
import dev.termlink.terminal.api.TerminalKittyKeyEventType
import dev.termlink.terminal.api.TerminalKittyKeyInput
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalRenderFrameV1
import dev.termlink.terminal.api.TerminalRenderFrameV3
import dev.termlink.terminal.api.TerminalSize

const val TERMLINK_TERMINAL_ABI_VERSION: Int = 1
const val TERMLINK_RENDER_FRAME_V1_FORMAT_VERSION: Int = 1
const val TERMLINK_RENDER_FRAME_V1_MAX_BYTES: Int = 16 * 1024 * 1024
const val TERMLINK_RENDER_FRAME_V3_FORMAT_VERSION: Int = 3
const val TERMLINK_RENDER_FRAME_V3_MAX_BYTES: Int = 16 * 1024 * 1024
const val TERMLINK_INPUT_V1_STRUCT_SIZE: Int = 56
const val TERMLINK_INPUT_V2_STRUCT_SIZE: Int = 96
const val TERMLINK_INPUT_V3_STRUCT_SIZE: Int = 160
const val TERMLINK_CONFIG_V3_STRUCT_SIZE: Int = 88
const val TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE: Int = 64
const val TERMLINK_HOST_EFFECT_FRAME_V2_FORMAT_VERSION: Int = 2
const val TERMLINK_HOST_EFFECT_FRAME_V2_MAX_BYTES: Int = 123_033_008

const val NATIVE_INPUT_KIND_KEY: Int = 1
const val NATIVE_INPUT_KIND_PASTE: Int = 2
const val NATIVE_INPUT_KIND_FOCUS_GAINED: Int = 3
const val NATIVE_INPUT_KIND_FOCUS_LOST: Int = 4
const val NATIVE_INPUT_KIND_WIN32_KEY: Int = 5
const val NATIVE_INPUT_KIND_KITTY_KEY: Int = 6
const val NATIVE_INPUT_KEY_CHARACTER: Long = 1
const val NATIVE_INPUT_KITTY_KEY_UNIDENTIFIED_TEXT: Long = 0xffff_ffffL

const val NATIVE_OUTPUT_PROFILE_UTF8: Int = 0
const val NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1: Int = 1

const val NATIVE_KITTY_EVENT_PRESS: Int = 1
const val NATIVE_KITTY_EVENT_REPEAT: Int = 2
const val NATIVE_KITTY_EVENT_RELEASE: Int = 3

const val NATIVE_KITTY_MODIFIER_SHIFT: Int = 1
const val NATIVE_KITTY_MODIFIER_ALT: Int = 2
const val NATIVE_KITTY_MODIFIER_CONTROL: Int = 4
const val NATIVE_KITTY_MODIFIER_SUPER: Int = 8
const val NATIVE_KITTY_MODIFIER_HYPER: Int = 16
const val NATIVE_KITTY_MODIFIER_META: Int = 32
const val NATIVE_KITTY_MODIFIER_CAPS_LOCK: Int = 64
const val NATIVE_KITTY_MODIFIER_NUM_LOCK: Int = 128

const val NATIVE_MOUSE_ACTION_PRESS: Int = 1
const val NATIVE_MOUSE_ACTION_RELEASE: Int = 2
const val NATIVE_MOUSE_ACTION_MOVE: Int = 3
const val NATIVE_MOUSE_ACTION_WHEEL: Int = 4

const val NATIVE_MOUSE_BUTTON_NONE: Int = 0
const val NATIVE_MOUSE_BUTTON_LEFT: Int = 1
const val NATIVE_MOUSE_BUTTON_MIDDLE: Int = 2
const val NATIVE_MOUSE_BUTTON_RIGHT: Int = 3

const val NATIVE_MOUSE_WHEEL_NONE: Int = 0
const val NATIVE_MOUSE_WHEEL_UP: Int = 1
const val NATIVE_MOUSE_WHEEL_DOWN: Int = 2
const val NATIVE_MOUSE_WHEEL_LEFT: Int = 3
const val NATIVE_MOUSE_WHEEL_RIGHT: Int = 4

const val NATIVE_MOUSE_BUTTON_HELD_LEFT: Int = 1
const val NATIVE_MOUSE_BUTTON_HELD_MIDDLE: Int = 2
const val NATIVE_MOUSE_BUTTON_HELD_RIGHT: Int = 4
const val NATIVE_MOUSE_POSITION_PIXEL: Int = 1

const val NATIVE_INPUT_MODIFIER_SHIFT: Int = 1
const val NATIVE_INPUT_MODIFIER_ALT: Int = 2
const val NATIVE_INPUT_MODIFIER_CONTROL: Int = 4
const val NATIVE_INPUT_MODIFIER_META: Int = 8

data class NativeTerminalLimits(
    val maxEventsPerBatch: Int = 256,
    val maxOutputBytesPerEvent: Int = 1024 * 1024,
    val maxOutputBytesPerBatch: Int = 4 * 1024 * 1024,
    val maxCheckpointBytes: Int = 64 * 1024 * 1024,
    val maxRenderFrameBytes: Int = TERMLINK_RENDER_FRAME_V1_MAX_BYTES,
    val maxColumns: Int = 512,
    val maxRows: Int = 512,
    val maxCells: Int = 262_144,
) {
    init {
        require(maxEventsPerBatch in 1..HARD_MAX_EVENTS_PER_BATCH)
        require(maxOutputBytesPerEvent in 1..HARD_MAX_OUTPUT_BYTES_PER_EVENT)
        require(maxOutputBytesPerBatch in maxOutputBytesPerEvent..HARD_MAX_OUTPUT_BYTES_PER_BATCH)
        require(maxCheckpointBytes in 1..HARD_MAX_BINARY_BYTES)
        require(maxRenderFrameBytes in 1..TERMLINK_RENDER_FRAME_V1_MAX_BYTES)
        require(maxColumns in 1..HARD_MAX_COLUMNS)
        require(maxRows in 1..HARD_MAX_ROWS)
        require(maxCells in 1..HARD_MAX_CELLS)
    }

    internal fun validateSize(size: TerminalSize, field: String) {
        require(size.columns <= maxColumns) { "$field columns exceed $maxColumns" }
        require(size.rows <= maxRows) { "$field rows exceed $maxRows" }
        val cells = size.columns.toLong() * size.rows.toLong()
        require(cells <= maxCells.toLong()) { "$field cell count exceeds $maxCells" }
    }

    private companion object {
        const val HARD_MAX_EVENTS_PER_BATCH = 256
        const val HARD_MAX_OUTPUT_BYTES_PER_EVENT = 1024 * 1024
        const val HARD_MAX_OUTPUT_BYTES_PER_BATCH = 4 * 1024 * 1024
        const val HARD_MAX_BINARY_BYTES = 64 * 1024 * 1024
        const val HARD_MAX_COLUMNS = 512
        const val HARD_MAX_ROWS = 512
        const val HARD_MAX_CELLS = 262_144
    }
}

sealed class NativeSourceEvent(open val sourceSequence: Long) {
    init {
        require(sourceSequence >= 0) { "source sequence must be non-negative" }
        require(sourceSequence < MAX_NEXT_SOURCE_SEQUENCE) {
            "source sequence has no valid next source sequence"
        }
    }

    class Output(
        override val sourceSequence: Long,
        bytes: ByteArray,
    ) : NativeSourceEvent(sourceSequence) {
        init {
            require(bytes.isNotEmpty()) { "output event must not be empty" }
        }

        private val ownedBytes = bytes.copyOf()

        val byteCount: Int
            get() = ownedBytes.size

        fun copyBytes(): ByteArray = ownedBytes.copyOf()

        internal fun copyForBatch(): Output = Output(sourceSequence, ownedBytes)
    }

    data class Resize(
        override val sourceSequence: Long,
        val size: TerminalSize,
    ) : NativeSourceEvent(sourceSequence)

    data class Advance(
        override val sourceSequence: Long,
        val micros: Long,
    ) : NativeSourceEvent(sourceSequence) {
        init {
            require(micros >= 0) { "advance micros must be non-negative" }
        }
    }

    data class Gap(
        override val sourceSequence: Long,
    ) : NativeSourceEvent(sourceSequence)

    private companion object {
        const val MAX_NEXT_SOURCE_SEQUENCE = Long.MAX_VALUE - 1L
    }
}

class NativeEventBatch private constructor(
    val events: List<NativeSourceEvent>,
    val outputByteCount: Int,
) {
    val firstSourceSequence: Long
        get() = events.first().sourceSequence

    val nextSourceSequence: Long
        get() = events.last().sourceSequence + 1L

    val size: Int
        get() = events.size

    internal fun copyForBridge(): NativeEventBatch = NativeEventBatch(
        events = events.map { event ->
            when (event) {
                is NativeSourceEvent.Output -> event.copyForBatch()
                is NativeSourceEvent.Resize -> event.copy()
                is NativeSourceEvent.Advance -> event.copy()
                is NativeSourceEvent.Gap -> event.copy()
            }
        },
        outputByteCount = outputByteCount,
    )

    companion object {
        fun of(
            events: List<NativeSourceEvent>,
            limits: NativeTerminalLimits = NativeTerminalLimits(),
        ): NativeEventBatch {
            require(events.isNotEmpty()) { "native event batch must not be empty" }
            require(events.size <= limits.maxEventsPerBatch) {
                "native event batch exceeds ${limits.maxEventsPerBatch} events"
            }

            var expected = events.first().sourceSequence
            var outputBytes = 0L
            val ownedEvents = ArrayList<NativeSourceEvent>(events.size)
            events.forEachIndexed { index, event ->
                require(event.sourceSequence == expected) {
                    "native event batch is discontinuous at index $index: " +
                        "expected $expected, received ${event.sourceSequence}"
                }
                when (event) {
                    is NativeSourceEvent.Output -> {
                        require(event.byteCount <= limits.maxOutputBytesPerEvent) {
                            "output event exceeds ${limits.maxOutputBytesPerEvent} bytes"
                        }
                        outputBytes += event.byteCount.toLong()
                        require(outputBytes <= limits.maxOutputBytesPerBatch.toLong()) {
                            "native event batch exceeds ${limits.maxOutputBytesPerBatch} output bytes"
                        }
                        ownedEvents += event.copyForBatch()
                    }
                    is NativeSourceEvent.Resize -> {
                        limits.validateSize(event.size, "resize")
                        ownedEvents += event.copy()
                    }
                    is NativeSourceEvent.Advance -> ownedEvents += event.copy()
                    is NativeSourceEvent.Gap -> ownedEvents += event.copy()
                }
                expected = event.sourceSequence + 1L
            }

            return NativeEventBatch(ownedEvents.toList(), outputBytes.toInt())
        }
    }
}

@JvmInline
value class NativeBridgeHandle(val value: Long) {
    init {
        require(value != 0L) { "native handle must not be zero" }
    }
}

data class NativeBridgeTerminal(
    val handle: NativeBridgeHandle,
    val nextSourceSequence: Long,
    val presentationRevision: Long,
)

data class NativeBridgeApplyReceipt(
    val nextSourceSequence: Long,
    val presentationRevision: Long,
)

/**
 * Field-for-field representation of the fixed 88-byte `TlTerminalConfigV3` C ABI.
 *
 * V3 deliberately requires [structSize] to equal 88 exactly; unlike V1/V2, an unknown tail is not
 * accepted. [columns], [rows], [outputProfile], and [reserved0] represent raw `uint32_t` fields.
 * A bridge must validate their unsigned interpretation before marshalling. Kotlin cannot represent
 * the upper half of `uint64_t`, so negative sequence/history values are rejected by the bridge.
 *
 * [JniNativeBridge] maps this contract to `tl_terminal_create_v3`; tests can inject a fake.
 */
data class NativeTerminalConfigV3(
    val abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION,
    val structSize: Int = TERMLINK_CONFIG_V3_STRUCT_SIZE,
    val columns: Int,
    val rows: Int,
    val initialSourceSequence: Long,
    val reservedV1Compat0: Long = 0,
    val reservedV1Compat1: Long = 0,
    val historyCapacityRows: Long = 0,
    val reservedV2Compat0: Long = 0,
    val reservedV2Compat1: Long = 0,
    val outputProfile: Int,
    val reserved0: Int = 0,
    val reserved1: Long = 0,
    val reserved2: Long = 0,
)

internal fun TerminalOutputProfile.toNativeOutputProfile(): Int = when (this) {
    TerminalOutputProfile.UTF8 -> NATIVE_OUTPUT_PROFILE_UTF8
    TerminalOutputProfile.UTF8_WITH_STANDALONE_C1 ->
        NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1
}

/** Atomic V2 apply receipt plus a detached, caller-owned encoded host-effect frame. */
class NativeBridgeApplyWithHostEffectsV2(
    val receipt: NativeBridgeApplyReceipt,
    hostEffectFrame: ByteArray,
) {
    private val ownedHostEffectFrame = hostEffectFrame.copyOf()

    val frameByteCount: Int
        get() = ownedHostEffectFrame.size

    fun copyHostEffectFrame(): ByteArray = ownedHostEffectFrame.copyOf()
}

data class NativeRenderRequest(
    val maxPayloadBytes: Int,
)

/**
 * Owned representation of the fixed 56-byte `TlTerminalInputV1` semantic-input ABI.
 *
 * Kotlin owns [data] and JNI only pins its copied value for the duration of one native call. Key
 * and focus inputs carry an empty array; paste carries the exact bytes supplied by the caller.
 */
class NativeTerminalInputV1(
    val abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION,
    val structSize: Int = TERMLINK_INPUT_V1_STRUCT_SIZE,
    val kind: Int,
    val key: Int = 0,
    val modifiers: Int = 0,
    val character: Int = 0,
    data: ByteArray = byteArrayOf(),
    val dataLength: Long = data.size.toLong(),
    val reserved0: Long = 0,
    val reserved1: Long = 0,
) {
    private val ownedData = data.copyOf()

    val dataByteCount: Int
        get() = ownedData.size

    fun copyData(): ByteArray = ownedData.copyOf()

    internal fun copyForBridge(): NativeTerminalInputV1 = NativeTerminalInputV1(
        abiVersion = abiVersion,
        structSize = structSize,
        kind = kind,
        key = key,
        modifiers = modifiers,
        character = character,
        data = ownedData,
        dataLength = dataLength,
        reserved0 = reserved0,
        reserved1 = reserved1,
    )
}

/**
 * Field-for-field representation of the fixed 64-byte `TlTerminalMouseInputV1` C ABI.
 *
 * JNI marshals these values without reinterpreting the semantic API.
 */
data class NativeMouseInputV1(
    val abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION,
    val structSize: Int = TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE,
    val action: Int,
    val button: Int,
    val wheel: Int,
    val buttonsHeld: Int,
    val positionFlags: Int,
    val modifiers: Int,
    val cellColumn: Int,
    val cellRow: Int,
    val pixelX: Int,
    val pixelY: Int,
    val reserved0: Long = 0,
    val reserved1: Long = 0,
)

/**
 * Field-for-field representation of the fixed 96-byte `TlTerminalInputV2` C ABI specialized for
 * `TL_INPUT_KIND_WIN32_KEY`.
 *
 * Kotlin has no unambiguous signed representation of every `uint32_t` value, so
 * [win32ControlKeyState] uses [Long]. A bridge implementation must validate that value against the
 * unsigned 32-bit range before marshalling it. The remaining Win32 values are restricted by the C
 * contract to unsigned 16-bit values, except [win32KeyDown], which must be zero or one.
 */
data class NativeWin32KeyInputV2(
    val abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION,
    val structSize: Int = TERMLINK_INPUT_V2_STRUCT_SIZE,
    val kind: Int = NATIVE_INPUT_KIND_WIN32_KEY,
    val key: Int = 0,
    val modifiers: Int = 0,
    val character: Int = 0,
    val data: Long = 0,
    val dataLength: Long = 0,
    val reservedV1Compat0: Long = 0,
    val reservedV1Compat1: Long = 0,
    val win32VirtualKey: Int,
    val win32ScanCode: Int,
    val win32UnicodeChar: Int,
    val win32KeyDown: Int,
    val win32ControlKeyState: Long,
    val win32RepeatCount: Int,
    val reserved0: Long = 0,
    val reserved1: Long = 0,
)

/**
 * Field-for-field semantic representation of the canonical 160-byte `TlTerminalInputV3` ABI.
 *
 * Bytes 0..95 are the frozen V2 prefix. For kind 6, [key] and [character] carry the Kitty key
 * identity and primary character scalar, while every Win32/payload field in that prefix is zero.
 * The V3 tail is laid out as event type at 96, modifiers at 100, shifted/base-layout scalars at
 * 104/108, an associated-text pointer at 112, its uint64 length at 120, and four reserved uint64
 * fields at 128. Kotlin deliberately owns the pointed-to UTF-8 bytes instead of exposing a raw
 * process address; JNI pins or copies them only for the native call.
 */
class NativeKittyKeyInputV3(
    val abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION,
    val structSize: Int = TERMLINK_INPUT_V3_STRUCT_SIZE,
    val kind: Int = NATIVE_INPUT_KIND_KITTY_KEY,
    val key: Long,
    val modifiers: Int = 0,
    val character: Int,
    val data: Long = 0,
    val dataLength: Long = 0,
    val reservedV1Compat0: Long = 0,
    val reservedV1Compat1: Long = 0,
    val win32VirtualKey: Int = 0,
    val win32ScanCode: Int = 0,
    val win32UnicodeChar: Int = 0,
    val win32KeyDown: Int = 0,
    val win32ControlKeyState: Long = 0,
    val win32RepeatCount: Int = 0,
    val reservedV2Prefix0: Long = 0,
    val reservedV2Prefix1: Long = 0,
    val kittyEventType: Int,
    val kittyModifiers: Int,
    val kittyShiftedKey: Int,
    val kittyBaseLayoutKey: Int,
    associatedText: ByteArray,
    val associatedTextLength: Long = associatedText.size.toLong(),
    val reservedV3Tail0: Long = 0,
    val reservedV3Tail1: Long = 0,
    val reservedV3Tail2: Long = 0,
    val reservedV3Tail3: Long = 0,
) {
    private val ownedAssociatedText = associatedText.copyOf()

    val associatedTextByteCount: Int
        get() = ownedAssociatedText.size

    fun copyAssociatedText(): ByteArray = ownedAssociatedText.copyOf()

    internal fun copyForBridge(): NativeKittyKeyInputV3 = NativeKittyKeyInputV3(
        abiVersion = abiVersion,
        structSize = structSize,
        kind = kind,
        key = key,
        modifiers = modifiers,
        character = character,
        data = data,
        dataLength = dataLength,
        reservedV1Compat0 = reservedV1Compat0,
        reservedV1Compat1 = reservedV1Compat1,
        win32VirtualKey = win32VirtualKey,
        win32ScanCode = win32ScanCode,
        win32UnicodeChar = win32UnicodeChar,
        win32KeyDown = win32KeyDown,
        win32ControlKeyState = win32ControlKeyState,
        win32RepeatCount = win32RepeatCount,
        reservedV2Prefix0 = reservedV2Prefix0,
        reservedV2Prefix1 = reservedV2Prefix1,
        kittyEventType = kittyEventType,
        kittyModifiers = kittyModifiers,
        kittyShiftedKey = kittyShiftedKey,
        kittyBaseLayoutKey = kittyBaseLayoutKey,
        associatedText = ownedAssociatedText,
        associatedTextLength = associatedTextLength,
        reservedV3Tail0 = reservedV3Tail0,
        reservedV3Tail1 = reservedV3Tail1,
        reservedV3Tail2 = reservedV3Tail2,
        reservedV3Tail3 = reservedV3Tail3,
    )
}

internal fun nativeKittyKeyInput(input: TerminalKittyKeyInput): NativeKittyKeyInputV3 {
    val (key, character) = when (val identity = input.key) {
        is TerminalKittyKey.Character -> NATIVE_INPUT_KEY_CHARACTER to identity.codePoint
        is TerminalKittyKey.Functional -> nativeKittyFunctionalKey(identity.value) to 0
        TerminalKittyKey.UnidentifiedText -> NATIVE_INPUT_KITTY_KEY_UNIDENTIFIED_TEXT to 0
    }
    return NativeKittyKeyInputV3(
        key = key,
        character = character,
        kittyEventType = when (input.eventType) {
            TerminalKittyKeyEventType.PRESS -> NATIVE_KITTY_EVENT_PRESS
            TerminalKittyKeyEventType.REPEAT -> NATIVE_KITTY_EVENT_REPEAT
            TerminalKittyKeyEventType.RELEASE -> NATIVE_KITTY_EVENT_RELEASE
        },
        kittyModifiers = with(input.modifiers) {
            (if (shift) NATIVE_KITTY_MODIFIER_SHIFT else 0) or
                (if (alt) NATIVE_KITTY_MODIFIER_ALT else 0) or
                (if (control) NATIVE_KITTY_MODIFIER_CONTROL else 0) or
                (if (superKey) NATIVE_KITTY_MODIFIER_SUPER else 0) or
                (if (hyper) NATIVE_KITTY_MODIFIER_HYPER else 0) or
                (if (meta) NATIVE_KITTY_MODIFIER_META else 0) or
                (if (capsLock) NATIVE_KITTY_MODIFIER_CAPS_LOCK else 0) or
                (if (numLock) NATIVE_KITTY_MODIFIER_NUM_LOCK else 0)
        },
        kittyShiftedKey = input.shiftedKey ?: 0,
        kittyBaseLayoutKey = input.baseLayoutKey ?: 0,
        associatedText = input.copyAssociatedTextUtf8(),
    )
}

internal fun nativeKittyFunctionalKey(key: TerminalKittyFunctionalKey): Long = when (key) {
    TerminalKittyFunctionalKey.ESCAPE -> 5
    TerminalKittyFunctionalKey.ENTER -> 2
    TerminalKittyFunctionalKey.TAB -> 4
    TerminalKittyFunctionalKey.BACKSPACE -> 3
    TerminalKittyFunctionalKey.INSERT -> 7
    TerminalKittyFunctionalKey.DELETE -> 6
    TerminalKittyFunctionalKey.ARROW_LEFT -> 14
    TerminalKittyFunctionalKey.ARROW_RIGHT -> 15
    TerminalKittyFunctionalKey.ARROW_UP -> 12
    TerminalKittyFunctionalKey.ARROW_DOWN -> 13
    TerminalKittyFunctionalKey.PAGE_UP -> 10
    TerminalKittyFunctionalKey.PAGE_DOWN -> 11
    TerminalKittyFunctionalKey.HOME -> 8
    TerminalKittyFunctionalKey.END -> 9
    TerminalKittyFunctionalKey.CAPS_LOCK -> 57_358
    TerminalKittyFunctionalKey.SCROLL_LOCK -> 57_359
    TerminalKittyFunctionalKey.NUM_LOCK -> 57_360
    TerminalKittyFunctionalKey.PRINT_SCREEN -> 57_361
    TerminalKittyFunctionalKey.PAUSE -> 57_362
    TerminalKittyFunctionalKey.MENU -> 57_363
    TerminalKittyFunctionalKey.F1 -> 16
    TerminalKittyFunctionalKey.F2 -> 17
    TerminalKittyFunctionalKey.F3 -> 18
    TerminalKittyFunctionalKey.F4 -> 19
    TerminalKittyFunctionalKey.F5 -> 20
    TerminalKittyFunctionalKey.F6 -> 21
    TerminalKittyFunctionalKey.F7 -> 22
    TerminalKittyFunctionalKey.F8 -> 23
    TerminalKittyFunctionalKey.F9 -> 24
    TerminalKittyFunctionalKey.F10 -> 25
    TerminalKittyFunctionalKey.F11 -> 26
    TerminalKittyFunctionalKey.F12 -> 27
    TerminalKittyFunctionalKey.F13 -> 57_376
    TerminalKittyFunctionalKey.F14 -> 57_377
    TerminalKittyFunctionalKey.F15 -> 57_378
    TerminalKittyFunctionalKey.F16 -> 57_379
    TerminalKittyFunctionalKey.F17 -> 57_380
    TerminalKittyFunctionalKey.F18 -> 57_381
    TerminalKittyFunctionalKey.F19 -> 57_382
    TerminalKittyFunctionalKey.F20 -> 57_383
    TerminalKittyFunctionalKey.F21 -> 57_384
    TerminalKittyFunctionalKey.F22 -> 57_385
    TerminalKittyFunctionalKey.F23 -> 57_386
    TerminalKittyFunctionalKey.F24 -> 57_387
    TerminalKittyFunctionalKey.F25 -> 57_388
    TerminalKittyFunctionalKey.F26 -> 57_389
    TerminalKittyFunctionalKey.F27 -> 57_390
    TerminalKittyFunctionalKey.F28 -> 57_391
    TerminalKittyFunctionalKey.F29 -> 57_392
    TerminalKittyFunctionalKey.F30 -> 57_393
    TerminalKittyFunctionalKey.F31 -> 57_394
    TerminalKittyFunctionalKey.F32 -> 57_395
    TerminalKittyFunctionalKey.F33 -> 57_396
    TerminalKittyFunctionalKey.F34 -> 57_397
    TerminalKittyFunctionalKey.F35 -> 57_398
    TerminalKittyFunctionalKey.KEYPAD_0 -> 100
    TerminalKittyFunctionalKey.KEYPAD_1 -> 101
    TerminalKittyFunctionalKey.KEYPAD_2 -> 102
    TerminalKittyFunctionalKey.KEYPAD_3 -> 103
    TerminalKittyFunctionalKey.KEYPAD_4 -> 104
    TerminalKittyFunctionalKey.KEYPAD_5 -> 105
    TerminalKittyFunctionalKey.KEYPAD_6 -> 106
    TerminalKittyFunctionalKey.KEYPAD_7 -> 107
    TerminalKittyFunctionalKey.KEYPAD_8 -> 108
    TerminalKittyFunctionalKey.KEYPAD_9 -> 109
    TerminalKittyFunctionalKey.KEYPAD_DECIMAL -> 110
    TerminalKittyFunctionalKey.KEYPAD_DIVIDE -> 115
    TerminalKittyFunctionalKey.KEYPAD_MULTIPLY -> 114
    TerminalKittyFunctionalKey.KEYPAD_SUBTRACT -> 113
    TerminalKittyFunctionalKey.KEYPAD_ADD -> 112
    TerminalKittyFunctionalKey.KEYPAD_ENTER -> 117
    TerminalKittyFunctionalKey.KEYPAD_EQUAL -> 116
    TerminalKittyFunctionalKey.KEYPAD_SEPARATOR -> 111
    TerminalKittyFunctionalKey.KEYPAD_ARROW_LEFT -> 57_417
    TerminalKittyFunctionalKey.KEYPAD_ARROW_RIGHT -> 57_418
    TerminalKittyFunctionalKey.KEYPAD_ARROW_UP -> 57_419
    TerminalKittyFunctionalKey.KEYPAD_ARROW_DOWN -> 57_420
    TerminalKittyFunctionalKey.KEYPAD_PAGE_UP -> 57_421
    TerminalKittyFunctionalKey.KEYPAD_PAGE_DOWN -> 57_422
    TerminalKittyFunctionalKey.KEYPAD_HOME -> 57_423
    TerminalKittyFunctionalKey.KEYPAD_END -> 57_424
    TerminalKittyFunctionalKey.KEYPAD_INSERT -> 57_425
    TerminalKittyFunctionalKey.KEYPAD_DELETE -> 57_426
    TerminalKittyFunctionalKey.KEYPAD_BEGIN -> 57_427
    TerminalKittyFunctionalKey.MEDIA_PLAY -> 57_428
    TerminalKittyFunctionalKey.MEDIA_PAUSE -> 57_429
    TerminalKittyFunctionalKey.MEDIA_PLAY_PAUSE -> 57_430
    TerminalKittyFunctionalKey.MEDIA_REVERSE -> 57_431
    TerminalKittyFunctionalKey.MEDIA_STOP -> 57_432
    TerminalKittyFunctionalKey.MEDIA_FAST_FORWARD -> 57_433
    TerminalKittyFunctionalKey.MEDIA_REWIND -> 57_434
    TerminalKittyFunctionalKey.MEDIA_TRACK_NEXT -> 57_435
    TerminalKittyFunctionalKey.MEDIA_TRACK_PREVIOUS -> 57_436
    TerminalKittyFunctionalKey.MEDIA_RECORD -> 57_437
    TerminalKittyFunctionalKey.LOWER_VOLUME -> 57_438
    TerminalKittyFunctionalKey.RAISE_VOLUME -> 57_439
    TerminalKittyFunctionalKey.MUTE_VOLUME -> 57_440
    TerminalKittyFunctionalKey.LEFT_SHIFT -> 57_441
    TerminalKittyFunctionalKey.LEFT_CONTROL -> 57_442
    TerminalKittyFunctionalKey.LEFT_ALT -> 57_443
    TerminalKittyFunctionalKey.LEFT_SUPER -> 57_444
    TerminalKittyFunctionalKey.LEFT_HYPER -> 57_445
    TerminalKittyFunctionalKey.LEFT_META -> 57_446
    TerminalKittyFunctionalKey.RIGHT_SHIFT -> 57_447
    TerminalKittyFunctionalKey.RIGHT_CONTROL -> 57_448
    TerminalKittyFunctionalKey.RIGHT_ALT -> 57_449
    TerminalKittyFunctionalKey.RIGHT_SUPER -> 57_450
    TerminalKittyFunctionalKey.RIGHT_HYPER -> 57_451
    TerminalKittyFunctionalKey.RIGHT_META -> 57_452
    TerminalKittyFunctionalKey.ISO_LEVEL3_SHIFT -> 57_453
    TerminalKittyFunctionalKey.ISO_LEVEL5_SHIFT -> 57_454
}

/** Raw encoded input whose byte-array ownership transfers to the caller. */
data class NativeBridgeEncodedInput(
    val payload: ByteArray,
)

/**
 * Raw bridge result. [payload] ownership transfers to the caller on return; the bridge must never
 * mutate it afterward. The handle wrapper strictly decodes it before exposing immutable state.
 */
data class NativeBridgeRenderFrame(
    val payload: ByteArray,
)

/**
 * Raw bridge result. [payload] ownership transfers to the caller on return; the bridge must never
 * mutate it afterward. The handle wrapper validates and copies every field before exposing it.
 */
data class NativeBridgeCheckpoint(
    val nextSourceSequence: Long,
    val presentationRevision: Long,
    val payload: ByteArray,
)

class NativeRenderFrame internal constructor(
    val renderFrame: TerminalRenderFrameV1,
) {
    val nextSourceSequence: Long
        get() = renderFrame.nextSourceSequence

    val presentationRevision: Long
        get() = renderFrame.presentationRevision

    val size: TerminalSize
        get() = renderFrame.size
}

/** Immutable Render Frame V3 returned by the production Android renderer boundary. */
class NativeRenderFrameV3 internal constructor(
    val renderFrame: TerminalRenderFrameV3,
) {
    val nextSourceSequence: Long
        get() = renderFrame.nextSourceSequence

    val presentationRevision: Long
        get() = renderFrame.presentationRevision

    val size: TerminalSize
        get() = renderFrame.size
}

class NativeCheckpoint internal constructor(
    val nextSourceSequence: Long,
    val presentationRevision: Long,
    payload: ByteArray,
) {
    private val ownedPayload = payload.copyOf()

    val payloadByteCount: Int
        get() = ownedPayload.size

    fun copyPayload(): ByteArray = ownedPayload.copyOf()
}

/**
 * Injectable boundary implemented by [JniNativeBridge] or by a pure Kotlin test fixture.
 *
 * Every input `ByteArray`, including output-event bytes reached through [NativeEventBatch] and an
 * imported checkpoint, is borrowed only for the duration of that call. Implementations must not
 * retain or mutate it. Every `ByteArray` in a successful return value transfers exclusively to the
 * caller; implementations must not retain a mutable alias or modify it after returning.
 *
 * [applyBatch] is atomic across the whole batch. Success commits every event. Failure commits no
 * event and exposes no successful prefix. The sole exception is a `GAP` failure, which may latch
 * the terminal's failed-closed flag but must not commit any preceding event state.
 */
interface NativeBridge {
    val abiVersion: Int
    val renderFrameFormatVersion: Int
    val maxRenderFrameBytes: Int
    /** Result of `tl_terminal_render_frame_v3_format_version`. */
    val renderFrameV3FormatVersion: Int
    /** Result of `tl_terminal_render_frame_v3_max_bytes`. */
    val maxRenderFrameV3Bytes: Int
    /** Result of `tl_terminal_host_effect_frame_v2_format_version`. */
    val hostEffectFrameV2FormatVersion: Int
    /** Result of `tl_terminal_host_effect_frame_v2_max_bytes`. */
    val maxHostEffectFrameV2Bytes: Int
    /** Result of the C ABI's `tl_terminal_mouse_input_v1_struct_size` discovery call. */
    val mouseInputV1StructSize: Int
    /** Result of the C ABI's `tl_terminal_input_v1_struct_size` discovery call. */
    val inputV1StructSize: Int
    /** Result of the C ABI's `tl_terminal_input_v2_struct_size` discovery call. */
    val inputV2StructSize: Int
    /** Result of the C ABI's `tl_terminal_input_v3_struct_size` discovery call. */
    val inputV3StructSize: Int
    /** Result of the C ABI's exact-size `tl_terminal_config_v3_struct_size` discovery call. */
    val configV3StructSize: Int

    fun create(
        size: TerminalSize,
        initialNextSourceSequence: Long,
    ): NativeBridgeResult<NativeBridgeTerminal>

    /** Maps directly to the exact-size `tl_terminal_create_v3` C ABI entry point. */
    fun createV3(config: NativeTerminalConfigV3): NativeBridgeResult<NativeBridgeTerminal>

    fun importCheckpoint(checkpoint: ByteArray): NativeBridgeResult<NativeBridgeTerminal>

    fun destroy(handle: NativeBridgeHandle): NativeBridgeResult<Unit>

    fun applyBatch(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyReceipt>

    /**
     * Maps to the atomic V2 host-effect batch apply. A failed result commits no candidate state and
     * carries no successful frame. A successful result transfers ownership of both receipt/frame.
     */
    fun applyBatchWithHostEffectsV2(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2>

    fun render(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame>

    /** Exports the canonical Render Frame V3 used by the production Android view. */
    fun renderV3(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame>

    /** Maps directly to the mode-aware `tl_terminal_encode_input_v1` C ABI call. */
    fun encodeInputV1(
        handle: NativeBridgeHandle,
        input: NativeTerminalInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput>

    /** Maps directly to the independent `tl_terminal_encode_mouse_input_v1` C ABI call. */
    fun encodeMouse(
        handle: NativeBridgeHandle,
        input: NativeMouseInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput>

    /** Maps directly to the independent `tl_terminal_encode_input_v2` C ABI call. */
    fun encodeInputV2(
        handle: NativeBridgeHandle,
        input: NativeWin32KeyInputV2,
    ): NativeBridgeResult<NativeBridgeEncodedInput>

    /** Maps directly to `tl_terminal_encode_input_v3` for Kitty semantic input. */
    fun encodeInputV3(
        handle: NativeBridgeHandle,
        input: NativeKittyKeyInputV3,
    ): NativeBridgeResult<NativeBridgeEncodedInput>

    fun exportCheckpoint(
        handle: NativeBridgeHandle,
        maxPayloadBytes: Int,
    ): NativeBridgeResult<NativeBridgeCheckpoint>
}

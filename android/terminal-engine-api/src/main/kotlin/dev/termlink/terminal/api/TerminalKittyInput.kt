package dev.termlink.terminal.api

import java.nio.CharBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

/** Event phase reported by the Kitty keyboard protocol. */
enum class TerminalKittyKeyEventType { PRESS, REPEAT, RELEASE }

/** The complete eight-bit Kitty modifier set. */
data class TerminalKittyModifiers(
    val shift: Boolean = false,
    val alt: Boolean = false,
    val control: Boolean = false,
    val superKey: Boolean = false,
    val hyper: Boolean = false,
    val meta: Boolean = false,
    val capsLock: Boolean = false,
    val numLock: Boolean = false,
)

/** Functional key identities supported by the native Kitty Input V3 contract. */
enum class TerminalKittyFunctionalKey {
    ESCAPE,
    ENTER,
    TAB,
    BACKSPACE,
    INSERT,
    DELETE,
    ARROW_LEFT,
    ARROW_RIGHT,
    ARROW_UP,
    ARROW_DOWN,
    PAGE_UP,
    PAGE_DOWN,
    HOME,
    END,
    CAPS_LOCK,
    SCROLL_LOCK,
    NUM_LOCK,
    PRINT_SCREEN,
    PAUSE,
    MENU,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24,
    F25,
    F26,
    F27,
    F28,
    F29,
    F30,
    F31,
    F32,
    F33,
    F34,
    F35,
    KEYPAD_0,
    KEYPAD_1,
    KEYPAD_2,
    KEYPAD_3,
    KEYPAD_4,
    KEYPAD_5,
    KEYPAD_6,
    KEYPAD_7,
    KEYPAD_8,
    KEYPAD_9,
    KEYPAD_DECIMAL,
    KEYPAD_DIVIDE,
    KEYPAD_MULTIPLY,
    KEYPAD_SUBTRACT,
    KEYPAD_ADD,
    KEYPAD_ENTER,
    KEYPAD_EQUAL,
    KEYPAD_SEPARATOR,
    KEYPAD_ARROW_LEFT,
    KEYPAD_ARROW_RIGHT,
    KEYPAD_ARROW_UP,
    KEYPAD_ARROW_DOWN,
    KEYPAD_PAGE_UP,
    KEYPAD_PAGE_DOWN,
    KEYPAD_HOME,
    KEYPAD_END,
    KEYPAD_INSERT,
    KEYPAD_DELETE,
    KEYPAD_BEGIN,
    MEDIA_PLAY,
    MEDIA_PAUSE,
    MEDIA_PLAY_PAUSE,
    MEDIA_REVERSE,
    MEDIA_STOP,
    MEDIA_FAST_FORWARD,
    MEDIA_REWIND,
    MEDIA_TRACK_NEXT,
    MEDIA_TRACK_PREVIOUS,
    MEDIA_RECORD,
    LOWER_VOLUME,
    RAISE_VOLUME,
    MUTE_VOLUME,
    LEFT_SHIFT,
    LEFT_CONTROL,
    LEFT_ALT,
    LEFT_SUPER,
    LEFT_HYPER,
    LEFT_META,
    RIGHT_SHIFT,
    RIGHT_CONTROL,
    RIGHT_ALT,
    RIGHT_SUPER,
    RIGHT_HYPER,
    RIGHT_META,
    ISO_LEVEL3_SHIFT,
    ISO_LEVEL5_SHIFT,
}

/** One layout-resolved Kitty key identity. */
sealed interface TerminalKittyKey {
    data class Character(val codePoint: Int) : TerminalKittyKey {
        init {
            requireKittyTextScalar(codePoint, "character")
        }
    }

    data class Functional(val value: TerminalKittyFunctionalKey) : TerminalKittyKey

    data object UnidentifiedText : TerminalKittyKey
}

/**
 * One semantic Kitty keyboard input event.
 *
 * Scalars are Unicode scalar values rather than UTF-16 code units. Associated text is validated as
 * strict UTF-8 and is bounded to 4,096 bytes and 1,024 scalars before it reaches a native bridge.
 */
data class TerminalKittyKeyInput(
    val key: TerminalKittyKey,
    val modifiers: TerminalKittyModifiers = TerminalKittyModifiers(),
    val eventType: TerminalKittyKeyEventType = TerminalKittyKeyEventType.PRESS,
    val shiftedKey: Int? = null,
    val baseLayoutKey: Int? = null,
    val associatedText: String = "",
) {
    init {
        shiftedKey?.let { requireKittyTextScalar(it, "shifted key") }
        baseLayoutKey?.let { requireKittyTextScalar(it, "base-layout key") }
        require(shiftedKey == null || modifiers.shift) {
            "a shifted key requires the Shift modifier"
        }
        require(key is TerminalKittyKey.Character || shiftedKey == null && baseLayoutKey == null) {
            "alternate key scalars are valid only for character identities"
        }

        val associatedBytes = encodeKittyUtf8(associatedText)
        require(associatedBytes.size <= MAX_ASSOCIATED_TEXT_BYTES) {
            "Kitty associated text exceeds $MAX_ASSOCIATED_TEXT_BYTES UTF-8 bytes"
        }
        val associatedScalars = associatedText.codePoints().toArray()
        require(associatedScalars.size <= MAX_ASSOCIATED_TEXT_SCALARS) {
            "Kitty associated text exceeds $MAX_ASSOCIATED_TEXT_SCALARS Unicode scalars"
        }
        associatedScalars.forEach { requireKittyTextScalar(it, "associated text") }
        require(eventType != TerminalKittyKeyEventType.RELEASE || associatedText.isEmpty()) {
            "a release event cannot carry associated text"
        }
        if (key === TerminalKittyKey.UnidentifiedText) {
            require(eventType == TerminalKittyKeyEventType.PRESS) {
                "unidentified text must be a press event"
            }
            require(associatedText.isNotEmpty()) {
                "unidentified text requires non-empty associated text"
            }
        }
    }

    /** Returns a fresh, strictly encoded caller-owned UTF-8 payload. */
    fun copyAssociatedTextUtf8(): ByteArray = encodeKittyUtf8(associatedText)

    companion object {
        const val MAX_ASSOCIATED_TEXT_BYTES: Int = 4_096
        const val MAX_ASSOCIATED_TEXT_SCALARS: Int = 1_024
    }
}

private fun requireKittyTextScalar(value: Int, field: String) {
    require(value in 0..0x10ffff && value !in 0xd800..0xdfff) {
        "$field is not a Unicode scalar value"
    }
    require(value !in 0x00..0x1f && value !in 0x7f..0x9f) {
        "$field contains a C0/C1 control scalar"
    }
}

private fun encodeKittyUtf8(value: String): ByteArray {
    val encoder = StandardCharsets.UTF_8.newEncoder()
        .onMalformedInput(CodingErrorAction.REPORT)
        .onUnmappableCharacter(CodingErrorAction.REPORT)
    val encoded = try {
        encoder.encode(CharBuffer.wrap(value))
    } catch (error: CharacterCodingException) {
        throw IllegalArgumentException("Kitty text contains malformed UTF-16", error)
    }
    return ByteArray(encoded.remaining()).also(encoded::get)
}

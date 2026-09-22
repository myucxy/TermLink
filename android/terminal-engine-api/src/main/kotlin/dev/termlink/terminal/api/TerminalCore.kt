package dev.termlink.terminal.api

import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import kotlinx.coroutines.flow.Flow

object TerminalCapabilities {
    const val PRESENTATION_METADATA_V1 = "terminal_presentation_metadata_v1"
    const val NORMALIZED_PRESENTATION_V1 = "terminal_presentation_v1"
    const val PRESENTATION_SNAPSHOT_V1 = "terminal_presentation_snapshot_v1"
    const val PRESENTATION_DELTA_V1 = "terminal_presentation_delta_v1"
    const val PRESENTATION_RESYNC_V1 = "terminal_presentation_resync_v1"
    const val TYPED_INPUT_V1 = "terminal_typed_input_v1"
}

enum class TerminalSourceEventKind { OUTPUT, RESIZE, TITLE, LIFECYCLE, INPUT_RESULT, GAP }

data class TerminalSourceEvent(
    val sourceEventSequence: Long,
    val kind: TerminalSourceEventKind,
    val payload: ByteArray,
) {
    init { require(sourceEventSequence > 0) }
}

enum class TerminalPresentationKind { SNAPSHOT, DELTA, RESYNC_REQUIRED }

data class TerminalPresentation(
    val sourceEventSequence: Long,
    val presentationRevision: Long,
    val kind: TerminalPresentationKind,
    val payload: ByteArray,
) {
    init {
        require(sourceEventSequence > 0)
        require(presentationRevision > 0)
    }
}

enum class TerminalKey {
    ENTER, TAB, BACKSPACE, ESCAPE, DELETE, INSERT, HOME, END,
    PAGE_UP, PAGE_DOWN, ARROW_UP, ARROW_DOWN, ARROW_LEFT, ARROW_RIGHT,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

data class TerminalInputModifiers(
    val control: Boolean = false,
    val alt: Boolean = false,
    val shift: Boolean = false,
    val meta: Boolean = false,
    val capsLock: Boolean = false,
    val numLock: Boolean = false,
)

sealed interface TerminalInput {
    data class Text(
        val value: String,
        val modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ) : TerminalInput

    data class Key(
        val value: TerminalKey,
        val modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ) : TerminalInput

    data class Paste(val bytes: ByteArray) : TerminalInput
    data class RawBytes(val bytes: ByteArray) : TerminalInput
}

interface ITerminalSessionSource {
    fun events(afterSourceEventSequence: Long): Flow<TerminalSourceEvent>
}

interface IOrderedTerminalJournal {
    suspend fun append(event: TerminalSourceEvent)
    fun replay(afterSourceEventSequence: Long): Flow<TerminalSourceEvent>
}

interface ITerminalPresentationProvider {
    suspend fun snapshot(throughSourceEventSequence: Long): TerminalPresentation
    suspend fun delta(afterRevision: Long, throughSourceEventSequence: Long): TerminalPresentation
}

interface ITerminalPresentationConsumer {
    val presentationRevision: Long
    suspend fun apply(update: TerminalPresentation)
    suspend fun requireResync(expectedRevision: Long)
}

fun interface ITerminalInputEncoder {
    fun encode(input: TerminalInput): ByteArray
}

interface ITerminalTransport {
    val capabilities: Set<String>
    suspend fun send(event: TerminalSourceEvent)
    suspend fun send(update: TerminalPresentation)
    suspend fun requestResync(expectedRevision: Long)
}

/** Default UTF-8/VT encoder shared by UI adapters and wire compatibility. */
object VtTerminalInputEncoder : ITerminalInputEncoder {
    override fun encode(input: TerminalInput): ByteArray = when (input) {
        is TerminalInput.RawBytes -> input.bytes.copyOf().also(::requireNotEmpty)
        is TerminalInput.Paste -> input.bytes.copyOf().also(::requireNotEmpty)
        is TerminalInput.Text -> encodeText(input.value, input.modifiers)
        is TerminalInput.Key -> encodeKey(input.value, input.modifiers)
    }

    private fun encodeText(value: String, modifiers: TerminalInputModifiers): ByteArray {
        require(value.isNotEmpty()) { "text input must not be empty" }
        val bytes = if (modifiers.control) {
            require(value.length == 1 && value[0].code <= 0x7f) {
                "control text must contain one ASCII character"
            }
            byteArrayOf(controlByte(value[0]))
        } else {
            val encoder = StandardCharsets.UTF_8.newEncoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
            val buffer = encoder.encode(java.nio.CharBuffer.wrap(value))
            ByteArray(buffer.remaining()).also(buffer::get)
        }
        return prefixEscape(bytes, modifiers)
    }

    private fun encodeKey(key: TerminalKey, modifiers: TerminalInputModifiers): ByteArray {
        val sequence = when (key) {
            TerminalKey.ENTER -> "\r"
            TerminalKey.TAB -> if (modifiers.shift) "\u001b[Z" else "\t"
            TerminalKey.BACKSPACE -> "\u007f"
            TerminalKey.ESCAPE -> "\u001b"
            TerminalKey.DELETE -> csiTilde(3, modifiers)
            TerminalKey.INSERT -> csiTilde(2, modifiers)
            TerminalKey.HOME -> csiFinal('H', modifiers)
            TerminalKey.END -> csiFinal('F', modifiers)
            TerminalKey.PAGE_UP -> csiTilde(5, modifiers)
            TerminalKey.PAGE_DOWN -> csiTilde(6, modifiers)
            TerminalKey.ARROW_UP -> csiFinal('A', modifiers)
            TerminalKey.ARROW_DOWN -> csiFinal('B', modifiers)
            TerminalKey.ARROW_RIGHT -> csiFinal('C', modifiers)
            TerminalKey.ARROW_LEFT -> csiFinal('D', modifiers)
            TerminalKey.F1 -> functionKey("P", 0, modifiers)
            TerminalKey.F2 -> functionKey("Q", 0, modifiers)
            TerminalKey.F3 -> functionKey("R", 0, modifiers)
            TerminalKey.F4 -> functionKey("S", 0, modifiers)
            TerminalKey.F5 -> functionKey(null, 15, modifiers)
            TerminalKey.F6 -> functionKey(null, 17, modifiers)
            TerminalKey.F7 -> functionKey(null, 18, modifiers)
            TerminalKey.F8 -> functionKey(null, 19, modifiers)
            TerminalKey.F9 -> functionKey(null, 20, modifiers)
            TerminalKey.F10 -> functionKey(null, 21, modifiers)
            TerminalKey.F11 -> functionKey(null, 23, modifiers)
            TerminalKey.F12 -> functionKey(null, 24, modifiers)
        }
        val bytes = sequence.toByteArray(StandardCharsets.US_ASCII)
        return if (key in setOf(TerminalKey.ENTER, TerminalKey.TAB, TerminalKey.BACKSPACE, TerminalKey.ESCAPE)) {
            prefixEscape(bytes, modifiers)
        } else {
            bytes
        }
    }

    private fun csiFinal(final: Char, modifiers: TerminalInputModifiers): String =
        modifierParameter(modifiers).let { if (it == 1) "\u001b[$final" else "\u001b[1;$it$final" }

    private fun csiTilde(key: Int, modifiers: TerminalInputModifiers): String =
        modifierParameter(modifiers).let { if (it == 1) "\u001b[$key~" else "\u001b[$key;$it~" }

    private fun functionKey(ss3Final: String?, tildeKey: Int, modifiers: TerminalInputModifiers): String {
        val parameter = modifierParameter(modifiers)
        return if (ss3Final != null) {
            if (parameter == 1) "\u001bO$ss3Final" else "\u001b[1;$parameter$ss3Final"
        } else {
            if (parameter == 1) "\u001b[$tildeKey~" else "\u001b[$tildeKey;$parameter~"
        }
    }

    private fun modifierParameter(value: TerminalInputModifiers): Int =
        1 + (if (value.shift) 1 else 0) + (if (value.alt) 2 else 0) +
            (if (value.control) 4 else 0) + (if (value.meta) 8 else 0)

    private fun prefixEscape(bytes: ByteArray, modifiers: TerminalInputModifiers): ByteArray =
        if (!modifiers.alt && !modifiers.meta) bytes else byteArrayOf(0x1b) + bytes

    private fun controlByte(value: Char): Byte {
        val upper = value.uppercaseChar()
        return when {
            upper in '@'..'_' -> (upper.code and 0x1f).toByte()
            upper == '?' -> 0x7f
            upper == ' ' -> 0
            else -> error("character has no terminal control encoding")
        }
    }

    private fun requireNotEmpty(bytes: ByteArray) {
        require(bytes.isNotEmpty()) { "terminal input must not be empty" }
    }
}

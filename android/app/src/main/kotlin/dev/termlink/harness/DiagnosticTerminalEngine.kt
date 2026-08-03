package dev.termlink.harness

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets

/** Phase 0 byte sink. It deliberately does not claim to emulate VT terminal state. */
class DiagnosticTerminalEngine : TerminalEngine {
    private val mutableInputEvents = MutableSharedFlow<ByteArray>(extraBufferCapacity = 16)
    private val mutableSizeEvents = MutableSharedFlow<TerminalSize>(extraBufferCapacity = 16)
    private val output = ByteArrayOutputStream()
    private val observedSequences = mutableListOf<Long>()
    private var disposed = false

    override val inputEvents: Flow<ByteArray> = mutableInputEvents
    override val sizeEvents: Flow<TerminalSize> = mutableSizeEvents

    var size: TerminalSize? = null
        private set

    val outputByteCount: Int
        get() = output.size()

    val feedCount: Int
        get() = observedSequences.size

    override suspend fun feed(seq: Long, bytes: ByteBuffer) {
        check(!disposed) { "Terminal engine is disposed" }
        val chunk = ByteArray(bytes.remaining())
        bytes.get(chunk)
        output.write(chunk)
        observedSequences += seq
    }

    override fun resize(columns: Int, rows: Int) {
        check(!disposed) { "Terminal engine is disposed" }
        TerminalSize(columns, rows).also {
            size = it
            mutableSizeEvents.tryEmit(it)
        }
    }

    override fun applyTheme(theme: TerminalThemeV1) = Unit

    override fun setReadOnly(readOnly: Boolean) = Unit

    override fun clear() {
        check(!disposed) { "Terminal engine is disposed" }
        output.reset()
        observedSequences.clear()
        size = null
    }

    override fun dispose() {
        disposed = true
        output.reset()
        observedSequences.clear()
        size = null
    }

    fun escapedPreview(maxCharacters: Int = 6000): String {
        val decoded = output.toByteArray().toString(StandardCharsets.UTF_8)
        val escaped = buildString(decoded.length) {
            decoded.forEach { character ->
                when (character) {
                    '\u001b' -> append("<ESC>")
                    '\u0007' -> append("<BEL>")
                    '\r' -> append("<CR>")
                    '\n', '\t' -> append(character)
                    else -> if (character.code < 0x20) {
                        append("<0x${character.code.toString(16).padStart(2, '0')}>")
                    } else {
                        append(character)
                    }
                }
            }
        }
        return if (escaped.length <= maxCharacters) escaped else escaped.takeLast(maxCharacters)
    }
}

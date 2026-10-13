package dev.termlink.terminal.api

/** A device-specific consumer must record an attempt before any OS side effect. */
interface TerminalPlatformActionSink : AutoCloseable {
    fun offer(effects: List<TerminalHostEffectV2>)
}

data class TerminalHostSignals(val progressState: Int = 0, val progressPercent: Int = 0) {
    init {
        require(progressState in 0..4 && progressPercent in 0..100 &&
            (progressState !in listOf(0, 3) || progressPercent == 0))
    }
    companion object {
        fun decode(bytes: ByteArray): TerminalHostSignals {
            require(bytes.size == 12 && bytes.copyOfRange(0,8).contentEquals(byteArrayOf(84,76,72,83,49,0,0,0)) && bytes[10] == 0.toByte() && bytes[11] == 0.toByte())
            return TerminalHostSignals(bytes[8].toInt() and 255, bytes[9].toInt() and 255)
        }
    }
}

/** Requests only. Callers must apply device consent, replay fencing, rate and audio policies. */
sealed interface TerminalPlatformAction {
    data object Bell : TerminalPlatformAction
    data class Sound(val volume: Int, val duration32nds: Int, val notes: List<Int>) : TerminalPlatformAction
    data class ClipboardWrite(val text: String) : TerminalPlatformAction
    companion object {
        fun decode(bytes: ByteArray): TerminalPlatformAction {
            require(bytes.size in 4..16392) { "Platform action length" }
            return when (bytes[0].toInt()) {
                1 -> { require(bytes.contentEquals(byteArrayOf(1,0,0,0))); Bell }
                2 -> {
                    val volume = bytes[1].toInt() and 255
                    val count = bytes[3].toInt() and 255
                    require(volume <= 7 && count in 1..30 && bytes.size == 4 + count)
                    val notes = bytes.drop(4).map { it.toInt() and 255 }
                    require(notes.all { it <= 25 })
                    Sound(volume, bytes[2].toInt() and 255, java.util.Collections.unmodifiableList(notes))
                }
                3 -> {
                    require(bytes.size >= 8 && bytes[1] == 0.toByte() && bytes[2] == 0.toByte() && bytes[3] == 0.toByte())
                    val length = java.nio.ByteBuffer.wrap(bytes, 4, 4).order(java.nio.ByteOrder.LITTLE_ENDIAN).int
                    require(length == bytes.size - 8 && bytes.drop(8).none { it == 0.toByte() })
                    val text = Charsets.UTF_8.newDecoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT)
                        .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT).decode(java.nio.ByteBuffer.wrap(bytes, 8, length)).toString()
                    ClipboardWrite(text)
                }
                else -> throw IllegalArgumentException("Platform action kind")
            }
        }
    }
}

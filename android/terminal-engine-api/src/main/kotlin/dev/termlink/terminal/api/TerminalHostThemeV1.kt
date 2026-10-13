package dev.termlink.terminal.api

/** Immutable ordered host palette, independent of the Android device theme. */
class TerminalHostThemeV1 private constructor(private val bytes: ByteArray) {
    fun encode(): ByteArray = bytes.copyOf()
    override fun equals(other: Any?): Boolean = other is TerminalHostThemeV1 && bytes.contentEquals(other.bytes)
    override fun hashCode(): Int = bytes.contentHashCode()

    companion object {
        const val ENCODED_BYTES = 1044
        fun decode(bytes: ByteArray): TerminalHostThemeV1 {
            require(bytes.size == ENCODED_BYTES && bytes[0].toInt() == 1 &&
                (1..3).all { bytes[it].toInt() == 0 } && bytes[4].toInt() in 1..2 &&
                (5..7).all { bytes[it].toInt() == 0 } && (11 until ENCODED_BYTES step 4).all { bytes[it].toInt() == 0 }) {
                "Invalid terminal host theme payload"
            }
            return TerminalHostThemeV1(bytes.copyOf())
        }
    }
}

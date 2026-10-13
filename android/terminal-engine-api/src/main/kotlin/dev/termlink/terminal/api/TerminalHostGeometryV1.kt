package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Ordered measurements from the selected display. Zero pairs mean unavailable. */
data class TerminalHostGeometryV1(
    val cellWidth: Int = 0,
    val cellHeight: Int = 0,
    val displayWidth: Int = 0,
    val displayHeight: Int = 0,
    val windowWidth: Int = 0,
    val windowHeight: Int = 0,
) {
    init {
        validatePair(cellWidth, cellHeight, 1024)
        validatePair(displayWidth, displayHeight, 32768)
        validatePair(windowWidth, windowHeight, 32768)
    }

    fun encode(): ByteArray = ByteBuffer.allocate(ENCODED_BYTES).order(ByteOrder.LITTLE_ENDIAN).apply {
        intArrayOf(1, cellWidth, cellHeight, displayWidth, displayHeight, windowWidth, windowHeight, 0)
            .forEach { putInt(it) }
    }.array()

    companion object {
        const val ENCODED_BYTES = 32
        fun decode(bytes: ByteArray): TerminalHostGeometryV1 {
            require(bytes.size == ENCODED_BYTES) { "Invalid terminal host geometry length" }
            val words = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            require(words.int == 1 && words.getInt(28) == 0) { "Invalid terminal host geometry header" }
            return TerminalHostGeometryV1(words.int, words.int, words.int, words.int, words.int, words.int)
        }
        private fun validatePair(width: Int, height: Int, maximum: Int) {
            require(width in 0..maximum && height in 0..maximum && (width == 0) == (height == 0)) {
                "Invalid terminal host geometry dimension pair"
            }
        }
    }
}

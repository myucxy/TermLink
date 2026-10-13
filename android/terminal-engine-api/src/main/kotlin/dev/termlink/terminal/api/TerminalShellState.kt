package dev.termlink.terminal.api

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.io.ByteArrayOutputStream

/** Shell output is untrusted metadata. Reading it never opens files or runs commands. */
data class TerminalShellPoint(val sequence: Long, val lineId: Long, val cellOffset: Long, val page: Int, val alternate: Boolean)
data class TerminalShellCommand(
    val id: Long, val phase: Int, val prompt: TerminalShellPoint,
    val input: TerminalShellPoint?, val output: TerminalShellPoint?, val end: TerminalShellPoint?,
    val exitCode: Int?, val cwdUri: String,
)
data class TerminalShellState(val cwdUri: String, val commands: List<TerminalShellCommand>, val nextCommandId: Long) {
    companion object {
        const val MAX_BYTES = 273696
        val EMPTY = TerminalShellState("", emptyList(), 1)
        private fun utf8(bytes: ByteArray): String = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes)).toString()

        /** TLSHV1, all lengths checked before allocation; the checkpoint sequence is authoritative. */
        fun decode(bytes: ByteArray, nextSequence: Long): TerminalShellState {
            require(bytes.size in 32..MAX_BYTES && nextSequence >= 0) { "Invalid shell state size/sequence" }
            val b = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            val magic = ByteArray(8).also(b::get)
            require(magic.contentEquals(byteArrayOf(84, 76, 83, 72, 86, 49, 0, 0))) { "Invalid shell state magic" }
            val next = b.long
            val cwdSize = b.int
            val count = b.int
            require(next > 0 && count in 0..64 && b.long == 0L) { "Invalid shell state header" }
            fun cwd(length: Int): String {
                require(length in 0..4096 && length <= b.remaining()) { "Invalid shell directory length" }
                return utf8(ByteArray(length).also(b::get)).also { if (it.isNotEmpty()) parseDirectoryUri(it) }
            }
            val current = cwd(cwdSize)
            var previousId = 0L
            var previousSequence = 0L
            val commands = ArrayList<TerminalShellCommand>(count)
            repeat(count) {
                require(b.remaining() >= 116) { "Truncated shell command" }
                val id = b.long
                val phase = b.get().toInt() and 255
                val flags = b.get().toInt() and 255
                val reserved = b.short.toInt()
                val code = b.int
                require(id > previousId && id < next && phase in 0..4 && flags and 15 == flags && reserved == 0 &&
                    (flags and 8 != 0 || code == 0) && (flags and 8 == 0 || flags and 4 != 0) &&
                    ((phase == 3) == (flags and 4 != 0)) && (phase != 0 || flags == 0) &&
                    (phase != 1 || flags == 1) && (phase != 2 || flags and 2 != 0)) { "Invalid shell command" }
                val points = (0..3).map { index ->
                    val sequence = b.long
                    val line = b.long
                    val cell = b.int.toLong() and 0xffff_ffffL
                    val page = b.get().toInt() and 255
                    val alternate = b.get().toInt() and 255
                    val pointReserved = b.short.toInt()
                    if (index != 0 && flags and (1 shl (index - 1)) == 0) {
                        require(sequence == 0L && line == 0L && cell == 0L && page == 0 && alternate == 0 && pointReserved == 0)
                        null
                    } else {
                        require(sequence in previousSequence until nextSequence && line >= 0 && page in 1..6 &&
                            alternate in 0..1 && pointReserved == 0 && (alternate == 0 || page == 1)) { "Invalid shell anchor" }
                        previousSequence = sequence
                        TerminalShellPoint(sequence, line, cell, page, alternate != 0)
                    }
                }
                val directory = cwd(b.int)
                commands += TerminalShellCommand(id, phase, points[0]!!, points[1], points[2], points[3],
                    code.takeIf { flags and 8 != 0 }, directory)
                previousId = id
            }
            require(!b.hasRemaining()) { "Trailing shell state bytes" }
            return TerminalShellState(current, commands.toList(), next)
        }

        private fun parseDirectoryUri(uri: String): Pair<String, String> {
            require(uri.startsWith("file://") && uri.toByteArray(Charsets.UTF_8).size <= 4096)
            val tail = uri.substring(7)
            val slash = tail.indexOf('/')
            require(slash >= 0)
            val host = tail.substring(0, slash)
            require(host.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it in ".-_" })
            require(uri.none { it.isISOControl() || it in "\\?#" })
            val raw = tail.substring(slash).toByteArray(Charsets.UTF_8)
            val decoded = ByteArrayOutputStream(raw.size)
            var i = 0
            while (i < raw.size) {
                if (raw[i].toInt() == 37) {
                    require(i + 2 < raw.size)
                    val hi = raw[i + 1].toInt().toChar().digitToIntOrNull(16)
                    val lo = raw[i + 2].toInt().toChar().digitToIntOrNull(16)
                    require(hi != null && lo != null)
                    decoded.write(hi * 16 + lo)
                    i += 3
                } else decoded.write(raw[i++].toInt())
            }
            val path = utf8(decoded.toByteArray())
            require(path.none { it.isISOControl() })
            return host to path
        }

        /** Only drive-local Windows paths. Remote hosts, UNC/device paths and ADS are never opened. */
        fun localWindowsDirectory(uri: String): String? = runCatching {
            val (host, path) = parseDirectoryUri(uri)
            require(host.isEmpty() || host.equals("localhost", true))
            require(path.length >= 4 && path[0] == '/' && path[1].isLetter() && path[1].code < 128 && path[2] == ':' && path[3] == '/')
            require(path.substring(3).none { it in "\\:<>\"|?*" })
            val parts = path.substring(4).split('/')
            require(parts.none { it == "." || it == ".." || it.endsWith('.') || it.endsWith(' ') })
            path.substring(1).replace('/', '\\')
        }.getOrNull()
    }
}

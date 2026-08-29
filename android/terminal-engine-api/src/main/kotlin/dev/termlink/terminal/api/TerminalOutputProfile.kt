package dev.termlink.terminal.api

/**
 * Selects how a terminal interprets output bytes in the C1 byte range.
 *
 * [UTF8] preserves TermLink's original byte-stream contract. In that profile, bytes are decoded
 * as UTF-8 and standalone bytes in `0x80..0x9f` are not terminal controls.
 * [UTF8_WITH_STANDALONE_C1] keeps UTF-8 continuation bytes authoritative while additionally
 * accepting standalone ECMA-48 C1 control aliases at UTF-8 boundaries.
 */
enum class TerminalOutputProfile {
    UTF8,
    UTF8_WITH_STANDALONE_C1,
}

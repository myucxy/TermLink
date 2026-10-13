package dev.termlink.terminal.api

enum class TerminalInputDiagnosticOrigin { NATIVE_ENCODING, ANDROID_VALIDATION }

/** One failed input encode. This format contains a single code point, never input text or paste bytes. */
class TerminalInputDiagnostic private constructor(
    val text: String,
    val origin: TerminalInputDiagnosticOrigin,
    val status: String,
    val kind: String,
    val reason: String,
    val key: String,
    val codepoint: String,
    val modifiers: String,
) {
    /** A readable scalar only when it cannot alter text direction, hide itself, or inject controls. */
    val characterPreview: String? get() {
        if (codepoint == "none") return null
        val scalar = codepoint.substring(2).toInt(16)
        if (!Character.isValidCodePoint(scalar) || Character.isISOControl(scalar) ||
            Character.isWhitespace(scalar) || Character.getType(scalar) in setOf(
                Character.FORMAT.toInt(), Character.SURROGATE.toInt(),
                Character.UNASSIGNED.toInt(), Character.PRIVATE_USE.toInt(),
                Character.NON_SPACING_MARK.toInt(), Character.ENCLOSING_MARK.toInt(),
            )
        ) return null
        return when (scalar) {
            '\\'.code -> "\\\\"
            '\''.code -> "\\'"
            else -> String(Character.toChars(scalar))
        }
    }

    companion object {
        private val reasonPattern = Regex("[a-z][a-z0-9_]{0,63}")
        private val keyPattern = Regex("[A-Za-z][A-Za-z0-9_]{0,47}|VK_[0-9A-F]{4}")
        private val codepointPattern = Regex("U\\+[0-9A-F]{4,6}")
        private val modifierPattern = Regex("0x[0-9A-F]{2}")

        /** Android rejects standalone C1 code points before native encoding to preserve input safety. */
        fun androidC1Blocked(codePoint: Int): TerminalInputDiagnostic {
            require(codePoint in 0x80..0x9f)
            val formatted = "U+" + codePoint.toString(16).uppercase().padStart(4, '0')
            return TerminalInputDiagnostic(
                text = "Android input validation\nreason=c1_text_character_blocked\ncodepoint=$formatted\n",
                origin = TerminalInputDiagnosticOrigin.ANDROID_VALIDATION,
                status = "unsupported",
                kind = "key",
                reason = "c1_text_character_blocked",
                key = "Character",
                codepoint = formatted,
                modifiers = "none",
            )
        }

        fun parse(text: String?): TerminalInputDiagnostic? {
            if (text == null || text.length !in 1..2048 || !text.endsWith('\n') ||
                text.any { it != '\n' && it.code !in 32..126 }
            ) return null
            val lines = text.dropLast(1).split('\n')
            if (lines.size != 8 || lines[0] != "TL-INPUT-DIAG/1" ||
                lines[1] != "direction=input_encoding"
            ) return null
            val names = listOf("status", "kind", "reason", "key", "codepoint", "modifiers")
            val values = names.mapIndexed { index, name ->
                val prefix = "$name="
                lines[index + 2].takeIf { it.startsWith(prefix) }?.removePrefix(prefix) ?: return null
            }
            val status = values[0]
            val kind = values[1]
            val reason = values[2]
            val key = values[3]
            val codepoint = values[4]
            val modifiers = values[5]
            if (status !in setOf("unsupported", "invalid_argument", "limit_exceeded", "failed_closed") ||
                kind !in setOf("key", "kitty_key", "win32_key", "paste", "mouse", "focus") ||
                !reasonPattern.matches(reason) ||
                key != "none" && !keyPattern.matches(key) ||
                codepoint != "none" && (!codepointPattern.matches(codepoint) ||
                    codepoint.substring(2).toIntOrNull(16)?.let {
                        it > 0x10ffff || it in Character.MIN_SURROGATE.code..Character.MAX_SURROGATE.code
                    } != false) ||
                modifiers != "none" && !modifierPattern.matches(modifiers)
            ) return null
            return TerminalInputDiagnostic(text, TerminalInputDiagnosticOrigin.NATIVE_ENCODING,
                status, kind, reason, key, codepoint, modifiers)
        }
    }
}

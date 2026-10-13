package dev.termlink.terminal.api

/** Bounded, escaped output evidence. Never include it in transport telemetry or server metadata. */
class TerminalProtocolDiagnostic private constructor(
    val text: String,
    val category: String,
    val reason: String,
    val sequence: String,
) {
    companion object {
        fun forHostExit(exitCode: Int?, text: String?): TerminalProtocolDiagnostic? =
            if (exitCode == 125) parse(text) else null

        fun parse(text: String?): TerminalProtocolDiagnostic? {
            if (text?.startsWith("TL-DIAG/3\n") == true) return parseExpanded(text)
            if (text == null || text.length !in 1..4096 ||
                text.any { it != '\n' && it.code !in 32..126 } ||
                (!text.startsWith("TL-DIAG/1\n") && !text.startsWith("TL-DIAG/2\n")) || !text.endsWith('\n')
            ) return null
            val version2 = text.startsWith("TL-DIAG/2\n")
            val fields = linkedMapOf<String, String>()
            for (line in text.substringAfter('\n').dropLast(1).split('\n')) {
                val split = line.indexOf('=')
                if (split <= 0 || fields.put(line.take(split), line.substring(split + 1)) != null) return null
            }
            val legacyFields = setOf("category", "reason", "family", "sequence", "hex", "start", "end",
                "observed_bytes", "hidden_bytes", "truncated_bytes")
            val extendedFields = setOf("detected_at", "last_byte", "last_codepoint", "core_revision", "source_dirty",
                "output_profile", "width_profile", "direction", "core_impact", "rejected_parameter_index", "rejected_parameter")
            if (fields.keys != legacyFields + (if (version2) extendedFields else emptySet())) return null
            val category = fields.getValue("category")
            if (category !in setOf("unsupported", "malformed", "limit_exceeded")) return null
            if (fields.getValue("family") !in setOf("CSI", "OSC", "DCS", "APC", "ESC", "C0", "C1", "TEXT")) return null
            val hex = fields.getValue("hex")
            if (!hex.matches(Regex("[0-9A-F]{2}(?: [0-9A-F]{2}){0,191}"))) return null
            val bytes = hex.split(' ').map { it.toInt(16) }
            val escaped = buildString {
                for (byte in bytes) append(when (byte) {
                    27 -> "<ESC>"
                    92 -> "\\\\"
                    in 32..126 -> byte.toChar().toString()
                    else -> "\\x" + byte.toString(16).uppercase().padStart(2, '0')
                })
            }
            if (fields.getValue("sequence") != escaped) return null
            fun count(key: String): ULong? = fields.getValue(key).takeIf {
                it.isNotEmpty() && it.all { char -> char in '0'..'9' }
            }?.toULongOrNull()
            fun position(key: String): Pair<ULong, ULong>? {
                val parts = fields.getValue(key).split(':')
                if (parts.size != 2 || parts.any { it.isEmpty() || it.any { char -> char !in '0'..'9' } }) return null
                val sequence = parts[0].toULongOrNull() ?: return null
                val offset = parts[1].toULongOrNull()?.takeIf { it <= UInt.MAX_VALUE.toULong() } ?: return null
                return sequence to offset
            }
            val start = position("start") ?: return null
            val end = position("end") ?: return null
            val observed = count("observed_bytes") ?: return null
            val hidden = count("hidden_bytes") ?: return null
            val truncated = count("truncated_bytes") ?: return null
            if (hidden > observed || truncated > observed - hidden ||
                observed - hidden - truncated != bytes.size.toULong() ||
                end.first < start.first || (end.first == start.first &&
                    (end.second <= start.second || end.second - start.second != observed))
            ) return null
            val reason = fields.getValue("reason")
            if (reason.isBlank() || reason.length > 256) return null
            if (version2) {
                val detection = position("detected_at") ?: return null
                if (detection.first != end.first || detection.second + 1uL != end.second) return null
                if (!fields.getValue("core_revision").matches(Regex("[0-9a-fA-F]{40}|unavailable")) ||
                    fields.getValue("source_dirty") !in setOf("true", "false", "unknown") ||
                    fields.getValue("output_profile") !in setOf("0", "1") || fields.getValue("width_profile") !in setOf("1", "2") ||
                    fields.getValue("direction") != "output" ||
                    fields.getValue("core_impact") !in setOf("failed_closed", "operation_rejected")) return null
                val lastByte = fields.getValue("last_byte")
                val codepoint = fields.getValue("last_codepoint")
                val parameterIndex = fields.getValue("rejected_parameter_index")
                val parameter = fields.getValue("rejected_parameter")
                if (parameterIndex == "unavailable") {
                    if (parameter != "unavailable") return null
                } else {
                    val index = parameterIndex.toIntOrNull()?.takeIf { it in 0..31 && it.toString() == parameterIndex }
                        ?: return null
                    val header = bytes.filter { (it >= 32 && it != 127) || it == 27 }
                    val prefixSize = when {
                        header.take(2) == listOf(27, 91) || header.take(2) == listOf(27, 80) -> 2
                        header.firstOrNull() == 155 || header.firstOrNull() == 144 -> 1
                        else -> return null
                    }
                    val body = header.drop(prefixSize).let { if (it.firstOrNull() in 60..63) it.drop(1) else it }
                    val numeric = body.takeWhile { it in 48..57 || it == 59 || it == 58 }
                        .map { it.toChar() }.joinToString("")
                    val expected = numeric.split(';', ':').getOrNull(index)?.ifEmpty { "<default>" } ?: return null
                    if (parameter != expected) return null
                }
                if (hidden > 0uL) {
                    if (lastByte != "redacted" || codepoint != "redacted") return null
                } else {
                    if (!lastByte.matches(Regex("[0-9A-F]{2}"))) return null
                    if (truncated == 0uL && lastByte != hex.takeLast(2)) return null
                    val expectedCodepoint = if (truncated != 0uL) "unavailable" else try {
                        val decoded = bytes.map { it.toByte() }.toByteArray().decodeToString(throwOnInvalidSequence = true)
                        "U+" + decoded.codePointBefore(decoded.length).toString(16).uppercase().padStart(4, '0')
                    } catch (_: Exception) { "invalid_or_incomplete_utf8" }
                    if (codepoint != expectedCodepoint) return null
                }
            }
            return TerminalProtocolDiagnostic(text, category, reason, fields.getValue("sequence"))
        }

        private fun parseExpanded(text: String): TerminalProtocolDiagnostic? {
            if (text.length > 8192 || !text.endsWith('\n') || text.any { it != '\n' && it.code !in 32..126 }) return null
            val fields = text.substringAfter('\n').dropLast(1).split('\n')
            val source = fields.singleOrNull { it.startsWith("expanded_source=") }?.substringAfter('=') ?: return null
            if (source !in setOf("macro", "mixed")) return null
            val base = fields.filterNot { it.startsWith("expanded_") }
            val metadata = setOf("expanded_source", "expanded_origins", "expanded_origin_omitted_bytes")
            val expanded = fields.filter { it.startsWith("expanded_") && it.substringBefore('=') !in metadata }.map { it.removePrefix("expanded_") }
            val outer = parse("TL-DIAG/2\n" + base.joinToString("\n", postfix = "\n")) ?: return null
            val inner = parse("TL-DIAG/2\n" + expanded.joinToString("\n", postfix = "\n")) ?: return null
            val values = expanded.associate { it.substringBefore('=') to it.substringAfter('=') }
            val outerValues = base.associate { it.substringBefore('=') to it.substringAfter('=') }
            if (listOf("core_revision", "source_dirty", "output_profile", "width_profile", "core_impact").any {
                values[it] != outerValues[it]
            }) return null
            val start = values.getValue("start").split(':').map { it.toULong() }
            val end = values.getValue("end").split(':').map { it.toULong() }
            if (outer.category != inner.category || outer.reason != inner.reason) return null
            val origins = fields.filter { it.startsWith("expanded_origins=") }
            val omitted = fields.filter { it.startsWith("expanded_origin_omitted_bytes=") }
            if (source == "macro") {
                if (origins.isNotEmpty() || omitted.isNotEmpty() || start[0] != end[0] || start[0] > 63u ||
                    start[1] >= 262144u || end[1] > 262144u) return null
            } else {
                if (origins.size != 1 || omitted.size != 1 || start != listOf(0uL, 0uL)) return null
                fun decimal(value: String): ULong? = value.takeIf { it.isNotEmpty() && it.all { c -> c in '0'..'9' } }?.toULongOrNull()
                val skipped = decimal(omitted.single().substringAfter('=')) ?: return null
                val runs = origins.single().substringAfter('=').split(';')
                if (runs.size !in 1..16 || (skipped != 0uL && runs.size != 16)) return null
                var count = skipped
                var hasMacro = false
                for (run in runs) {
                    val parts = run.split(',')
                    if (parts.size != 4 || parts[0] !in setOf("macro", "output")) return null
                    val id = decimal(parts[1]) ?: return null
                    val offset = decimal(parts[2]) ?: return null
                    val length = decimal(parts[3]) ?: return null
                    val limit = if (parts[0] == "macro") 262144uL else UInt.MAX_VALUE.toULong()
                    if (length == 0uL || offset >= limit || length > limit - offset || ULong.MAX_VALUE - count < length) return null
                    if (parts[0] == "macro") { if (id > 63u) return null; hasMacro = true }
                    count += length
                }
                // If all macro origins fell beyond the bounded runs, the omission
                // count explicitly says those coordinates were not retained.
                if ((!hasMacro && skipped == 0uL) || count != values.getValue("observed_bytes").toULong() ||
                    end[0] != (count - 1uL) / UInt.MAX_VALUE.toULong() ||
                    end[1] != (count - 1uL) % UInt.MAX_VALUE.toULong() + 1uL) return null
            }
            return TerminalProtocolDiagnostic(text, outer.category, outer.reason,
                outer.sequence + (if (source == "macro") " [macro ${start[0]}: ${inner.sequence}]" else " [expanded: ${inner.sequence}]"))
        }
    }
}

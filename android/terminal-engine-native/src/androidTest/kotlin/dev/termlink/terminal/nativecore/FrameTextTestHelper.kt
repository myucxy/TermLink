package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalTextFrame

/** Test-only text projection; androidTest cannot access the main source set's internal helper. */
internal fun frameText(frame: TerminalTextFrame): String = buildString {
    frame.rows.forEachIndexed { rowIndex, row ->
        var lastNonBlank = -1
        val values = row.cells.mapIndexed { column, cell ->
            val text = if (cell.wideContinuation) "" else frame.graphemes.getOrNull(cell.graphemeId - 1)?.text ?: " "
            if (text.isNotBlank()) lastNonBlank = column
            text
        }
        val end = maxOf(row.meaningfulEnd - 1, lastNonBlank)
        if (end >= 0) values.take(end + 1).forEach { append(it) }
        if (!row.softWrapped && rowIndex != frame.rows.lastIndex) append('\n')
    }
}

package dev.termlink.terminal.termux

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import dev.termlink.terminal.replay.GoldenStreamBundle
import dev.termlink.terminal.replay.GoldenStreamReplayer
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import org.junit.Test
import java.io.File
import java.nio.ByteBuffer
import kotlin.test.assertEquals

class GoldenGridDraftExportTest {
    @Test
    fun exportsReferenceGridsForXtermComparison() = runTest {
        REFERENCE_CASES.forEach { caseName ->
            val engine = TermuxTerminalEngine()
            val checkpointingEngine = CheckpointingEngine(engine)
            try {
                val report = GoldenStreamReplayer().replay(
                    loadGoldenBundle(caseName),
                    checkpointingEngine,
                )
                val draft = engine.gridDraft()

                assertEquals(report.finalSize, draft.size)
                assertEquals(report.finalSize.rows, draft.rows.size)

                val destination = File(
                    System.getProperty("user.dir"),
                    "build/termlink-reference/${report.caseName}.termux-grid.json",
                )
                requireNotNull(destination.parentFile).mkdirs()
                destination.writeText(
                    Json { prettyPrint = true }.encodeToString(
                        JsonObject.serializer(),
                        draft.toJson(report.captureId, report.caseName, report.finalSequence),
                    ) + "\n",
                    Charsets.UTF_8,
                )

                val timelineDestination = File(
                    System.getProperty("user.dir"),
                    "build/termlink-reference/${report.caseName}.termux-timeline.json",
                )
                timelineDestination.writeText(
                    Json { prettyPrint = true }.encodeToString(
                        JsonObject.serializer(),
                        buildJsonObject {
                            put("format", JsonPrimitive("termlink.terminal-grid-timeline-draft"))
                            put("format_version", JsonPrimitive(0))
                            put("source", buildJsonObject {
                                put("case_name", JsonPrimitive(report.caseName))
                                put("capture_id", JsonPrimitive(report.captureId))
                            })
                            put("checkpoints", buildJsonArray {
                                checkpointingEngine.checkpoints.forEach { checkpoint ->
                                    add(buildJsonObject {
                                        put("sequence", JsonPrimitive(checkpoint.sequence))
                                        put(
                                            "grid",
                                            checkpoint.grid.toJson(
                                                report.captureId,
                                                report.caseName,
                                                checkpoint.sequence,
                                            ),
                                        )
                                    })
                                }
                            })
                        },
                    ) + "\n",
                    Charsets.UTF_8,
                )
            } finally {
                engine.dispose()
            }
        }
    }

    private fun loadGoldenBundle(caseName: String): GoldenStreamBundle {
        val base = "terminal-golden-streams/$caseName"
        return GoldenStreamBundle(
            manifestBytes = resourceBytes("$base/manifest.json"),
            eventsBytes = resourceBytes("$base/events.jsonl"),
            outputBytes = resourceBytes("$base/output.bin"),
            expectationsBytes = resourceBytes("$base/expectations.json"),
        )
    }

    private fun resourceBytes(path: String): ByteArray = requireNotNull(
        javaClass.classLoader?.getResourceAsStream(path),
    ) { "Missing test resource: $path" }.use { it.readBytes() }

    private companion object {
        val REFERENCE_CASES = listOf(
            "ansi-unicode-pwsh-v1",
            "smoke-cmd-v1",
            "smoke-powershell-v1",
            "smoke-pwsh-v1",
            "smoke-wsl-v1",
            "terminal-features-pwsh-v1",
            "tui-fzf-wsl-v1",
            "tui-htop-wsl-v1",
            "tui-less-wsl-v1",
            "tui-vim-wsl-v1",
        )
    }
}

private data class TermuxGridCheckpoint(
    val sequence: Long,
    val grid: TermuxGridDraft,
)

private class CheckpointingEngine(
    private val delegate: TermuxTerminalEngine,
) : TerminalEngine {
    val checkpoints = mutableListOf<TermuxGridCheckpoint>()

    override val inputEvents: Flow<ByteArray> = delegate.inputEvents
    override val sizeEvents: Flow<TerminalSize> = delegate.sizeEvents

    override suspend fun feed(seq: Long, bytes: ByteBuffer) {
        delegate.feed(seq, bytes)
        checkpoints += TermuxGridCheckpoint(seq, delegate.gridDraft())
    }

    override fun resize(columns: Int, rows: Int) = delegate.resize(columns, rows)

    override fun applyTheme(theme: TerminalThemeV1) = delegate.applyTheme(theme)

    override fun setReadOnly(readOnly: Boolean) = delegate.setReadOnly(readOnly)

    override fun clear() {
        checkpoints.clear()
        delegate.clear()
    }

    override fun dispose() = Unit
}

private fun TermuxGridDraft.toJson(
    captureId: String,
    caseName: String,
    finalSequence: Long,
): JsonObject =
    buildJsonObject {
        put("format", JsonPrimitive("termlink.terminal-grid-draft"))
        put("format_version", JsonPrimitive(0))
        put("engine", buildJsonObject {
            put("name", JsonPrimitive("termux-terminal"))
            put("version", JsonPrimitive("3df69d1da197dd9bd71a3bafd902dffd720576b4"))
            put("unicode_version", JsonNull)
        })
        put("source", buildJsonObject {
            put("case_name", JsonPrimitive(caseName))
            put("capture_id", JsonPrimitive(captureId))
            put("final_sequence", JsonPrimitive(finalSequence))
        })
        put("size", buildJsonObject {
            put("columns", JsonPrimitive(size.columns))
            put("rows", JsonPrimitive(size.rows))
        })
        put("cursor", buildJsonObject {
            put("column", JsonPrimitive(cursor.column))
            put("row", JsonPrimitive(cursor.row))
            put("visible", JsonPrimitive(cursor.visible))
            put("style", JsonPrimitive(cursor.style))
        })
        put("modes", buildJsonObject {
            put("alternate_buffer", JsonPrimitive(modes.alternateBuffer))
            put("application_cursor", JsonPrimitive(modes.applicationCursor))
            put("application_keypad", JsonPrimitive(modes.applicationKeypad))
            put("mouse_tracking", JsonPrimitive(modes.mouseTracking))
            put("reverse_video", JsonPrimitive(modes.reverseVideo))
        })
        put("rows", buildJsonArray {
            rows.forEach { row -> add(row.toJson()) }
        })
    }

private fun TermuxGridRow.toJson(): JsonObject = buildJsonObject {
    put("index", JsonPrimitive(index))
    put("text", JsonPrimitive(text))
    put("wrapped", JsonPrimitive(wrapped))
    put("style_runs", JsonArray(styleRuns.map(TermuxStyleRun::toJson)))
}

private fun TermuxStyleRun.toJson(): JsonObject = buildJsonObject {
    put("start", JsonPrimitive(start))
    put("end", JsonPrimitive(end))
    put("fg", style.foreground.toJson())
    put("bg", style.background.toJson())
    put("attrs", buildJsonObject {
        put("bold", JsonPrimitive(style.attributes.bold))
        put("dim", JsonPrimitive(style.attributes.dim))
        put("italic", JsonPrimitive(style.attributes.italic))
        put("underline", JsonPrimitive(style.attributes.underline))
        put("blink", JsonPrimitive(style.attributes.blink))
        put("inverse", JsonPrimitive(style.attributes.inverse))
        put("invisible", JsonPrimitive(style.attributes.invisible))
        put("strikethrough", JsonPrimitive(style.attributes.strikethrough))
    })
}

private fun TermuxCellColor.toJson(): JsonObject = buildJsonObject {
    put("type", JsonPrimitive(type))
    put("value", value?.let(::JsonPrimitive) ?: JsonNull)
}

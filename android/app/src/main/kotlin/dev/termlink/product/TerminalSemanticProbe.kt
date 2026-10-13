package dev.termlink.product

import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalShellPoint
import dev.termlink.terminal.api.TerminalShellState
import dev.termlink.terminal.api.TerminalTextFrame
import java.security.MessageDigest
import java.util.Locale
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Bounded, content-redacted view of the actual native presentation for debug acceptance. */
internal object TerminalSemanticProbe {
    private const val MAX_COLUMNS = 60
    private const val MAX_ROWS = 12
    private const val MAX_IMAGES = 4
    private const val MAX_PALETTE_ENTRIES = 16

    fun summarizeShell(state: TerminalShellState): JsonObject = buildJsonObject {
        put("cwd_path_sha256", shellPathHash(state.cwdUri)?.let { JsonPrimitive(it) } ?: JsonNull)
        put("command_count", state.commands.size)
        put("next_command_id", state.nextCommandId)
        put("commands", JsonArray(state.commands.takeLast(8).map { command ->
            buildJsonObject {
                put("id", command.id)
                put("phase", command.phase)
                put("exit_code", command.exitCode?.let { JsonPrimitive(it) } ?: JsonNull)
                put("cwd_path_sha256", shellPathHash(command.cwdUri)?.let { JsonPrimitive(it) } ?: JsonNull)
                put("prompt", shellPoint(command.prompt))
                put("input", command.input?.let(::shellPoint) ?: JsonNull)
                put("output", command.output?.let(::shellPoint) ?: JsonNull)
                put("end", command.end?.let(::shellPoint) ?: JsonNull)
            }
        }))
    }

    private fun shellPathHash(uri: String): String? {
        val path = TerminalShellState.localWindowsDirectory(uri) ?: return null
        val normalized = path.replace('/', '\\').let {
            if (it.length > 3) it.trimEnd('\\') else it
        }.lowercase(Locale.ROOT)
        return sha256(normalized.toByteArray(Charsets.UTF_8))
    }

    private fun shellPoint(point: TerminalShellPoint): JsonObject = buildJsonObject {
        put("sequence", point.sequence)
        put("line_id", point.lineId)
        put("cell_offset", point.cellOffset)
        put("page", point.page)
        put("alternate", point.alternate)
    }

    fun summarize(frame: TerminalTextFrame): JsonObject {
        val rows = frame.rows.take(MAX_ROWS)
        val columns = minOf(frame.size.columns, MAX_COLUMNS)
        val observedCells = rows.flatMap { it.cells.take(columns) }
        val renditionIds = observedCells.mapTo(sortedSetOf()) { it.renditionId }
        val graphemeHashes = HashMap<Int, String>()
        val graphics = frame as? TerminalRenderFrameV5
        val images = graphics?.images?.take(MAX_IMAGES).orEmpty()
        val glyphs = graphics?.glyphs?.filter { it.row in rows.indices && it.column in 0 until columns }.orEmpty()
        val resourceIds = (images.map { it.resourceId } + glyphs.map { it.resourceId }).toSet()

        return buildJsonObject {
            put("format_version", frame.formatVersion)
            put("width_profile", frame.widthProfile.name)
            put("presentation_revision", frame.presentationRevision)
            put("next_source_sequence", frame.nextSourceSequence)
            put("columns", frame.size.columns)
            put("rows", frame.size.rows)
            put("observed_columns", columns)
            put("observed_rows", rows.size)
            put("active_screen", frame.activeScreen.name)
            put("reverse_video", frame.reverseVideo)
            put("title_sha256", sha256(frame.title.toByteArray(Charsets.UTF_8)))
            put("cursor", buildJsonObject {
                put("row", frame.cursor.row)
                put("column", frame.cursor.column)
                put("visible", frame.cursor.visible)
                put("wrap_pending", frame.cursor.wrapPending)
                put("shape", frame.cursor.shape.name)
                put("blinking", frame.cursor.blinking)
            })
            put("screen_rows", JsonArray(rows.mapIndexed { rowIndex, row ->
                buildJsonObject {
                    put("row", rowIndex)
                    put("line_rendition", row.lineRendition.name)
                    put("meaningful_end", row.meaningfulEnd)
                    put("soft_wrapped", row.softWrapped)
                    put("cells", JsonArray(row.cells.take(columns).mapIndexed { column, cell ->
                        buildJsonObject {
                            put("column", column)
                            put("grapheme_id", cell.graphemeId)
                            put("rendition_id", cell.renditionId)
                            put("wide_continuation", cell.wideContinuation)
                            put("protected", cell.isProtected)
                            val grapheme = if (cell.wideContinuation) null else
                                requireNotNull(frame.graphemes.getOrNull(cell.graphemeId - 1)) { "invalid grapheme reference" }
                            if (grapheme != null) {
                                put("grapheme_sha256", graphemeHashes.getOrPut(cell.graphemeId) {
                                    sha256(grapheme.text.toByteArray(Charsets.UTF_8))
                                })
                                put("display_width", grapheme.displayWidth)
                            }
                        }
                    }))
                }
            }))
            put("renditions", JsonArray(renditionIds.map { id ->
                val rendition = requireNotNull(frame.renditions.getOrNull(id)) { "invalid rendition reference" }
                buildJsonObject {
                    put("id", id)
                    put("foreground", color(rendition.foreground))
                    put("background", color(rendition.background))
                    put("underline_color", color(rendition.underlineColor))
                    put("underline_style", rendition.underlineStyle.name)
                    put("bold", rendition.bold)
                    put("faint", rendition.faint)
                    put("italic", rendition.italic)
                    put("blink", rendition.blink)
                    put("inverse", rendition.inverse)
                    put("conceal", rendition.conceal)
                    put("strikethrough", rendition.strikethrough)
                    put("overline", rendition.overline)
                }
            }))
            put("colors", buildJsonObject {
                val palette = frame.palette
                put("default_foreground_rgb", palette.defaultForegroundRgb?.let { JsonPrimitive(it) } ?: JsonNull)
                put("default_background_rgb", palette.defaultBackgroundRgb?.let { JsonPrimitive(it) } ?: JsonNull)
                put("default_cursor_rgb", palette.defaultCursorRgb?.let { JsonPrimitive(it) } ?: JsonNull)
                put("selection_background_rgb", palette.selectionBackgroundRgb?.let { JsonPrimitive(it) } ?: JsonNull)
                put("foreground_alias", palette.foregroundAlias?.let { JsonPrimitive(it) } ?: JsonNull)
                put("background_alias", palette.backgroundAlias?.let { JsonPrimitive(it) } ?: JsonNull)
                put("frame_foreground_alias", palette.frameForegroundAlias?.let { JsonPrimitive(it) } ?: JsonNull)
                put("frame_background_alias", palette.frameBackgroundAlias?.let { JsonPrimitive(it) } ?: JsonNull)
                put("palette_entry_count", palette.colorsRgb.count { it != null })
                put("palette_entries", JsonArray(palette.colorsRgb.withIndex().asSequence()
                    .filter { it.value != null }.take(MAX_PALETTE_ENTRIES).map { entry ->
                        buildJsonObject {
                            put("index", entry.index)
                            put("rgb", requireNotNull(entry.value))
                        }
                    }.toList()))
            })
            if (graphics != null) {
                put("image_count", graphics.images.size)
                put("glyph_count", graphics.glyphs.size)
                put("resource_count", graphics.resources.entries.size)
                put("images", JsonArray(images.map { image ->
                    buildJsonObject {
                        put("placement_id", java.lang.Long.toUnsignedString(image.placementId))
                        put("resource_id", java.lang.Long.toUnsignedString(image.resourceId))
                        put("protocol", image.protocol.name)
                        put("column", image.column)
                        put("row", image.row)
                        put("z", image.z)
                        put("image_id", image.imageId)
                        put("source_x", image.source.x)
                        put("source_y", image.source.y)
                        put("source_width", image.source.width)
                        put("source_height", image.source.height)
                        put("x", ratio(image.x.numerator, image.x.denominator))
                        put("y", ratio(image.y.numerator, image.y.denominator))
                        put("width", ratio(image.width.numerator, image.width.denominator))
                        put("height", ratio(image.height.numerator, image.height.denominator))
                        put("allocated_columns", image.allocatedColumns)
                        put("allocated_rows", image.allocatedRows)
                        put("clip_left", image.clip.left)
                        put("clip_top", image.clip.top)
                        put("clip_right", image.clip.right)
                        put("clip_bottom", image.clip.bottom)
                    }
                }))
                put("glyphs", JsonArray(glyphs.map { glyph ->
                    buildJsonObject {
                        put("column", glyph.column)
                        put("row", glyph.row)
                        put("resource_id", java.lang.Long.toUnsignedString(glyph.resourceId))
                        put("x", glyph.x)
                        put("width", glyph.width)
                        put("height", glyph.height)
                    }
                }))
                put("resources", JsonArray(graphics.resources.entries.filter { it.id in resourceIds }.map { resource ->
                    buildJsonObject {
                        put("id", java.lang.Long.toUnsignedString(resource.id))
                        put("format", resource.format.name)
                        put("width", resource.width)
                        put("height", resource.height)
                        put("pixel_bytes", resource.pixelByteCount)
                        val digest = MessageDigest.getInstance("SHA-256")
                        digest.update(resource.readOnlyPixels())
                        put("pixels_sha256", digest.digest().toHex())
                    }
                }))
            }
        }
    }

    private fun color(value: TerminalRenderColorV1): JsonObject = buildJsonObject {
        when (value) {
            TerminalRenderColorV1.Default -> put("kind", "default")
            is TerminalRenderColorV1.Indexed -> {
                put("kind", "indexed")
                put("index", value.index)
            }
            is TerminalRenderColorV1.Rgb -> {
                put("kind", "rgb")
                put("rgb", value.value)
            }
        }
    }

    private fun ratio(numerator: Long, denominator: Long): JsonObject = buildJsonObject {
        put("numerator", numerator)
        put("denominator", denominator)
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).toHex()

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it.toInt() and 0xff) }
}

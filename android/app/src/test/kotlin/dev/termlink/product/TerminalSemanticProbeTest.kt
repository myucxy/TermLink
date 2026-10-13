package dev.termlink.product

import dev.termlink.terminal.api.TerminalGraphicsFormat
import dev.termlink.terminal.api.TerminalGraphicsResource
import dev.termlink.terminal.api.TerminalGraphicsResources
import dev.termlink.terminal.api.TerminalLineRendition
import dev.termlink.terminal.api.TerminalRenderCellV1
import dev.termlink.terminal.api.TerminalRenderClipV5
import dev.termlink.terminal.api.TerminalRenderColorV1
import dev.termlink.terminal.api.TerminalRenderCursorShapeV2
import dev.termlink.terminal.api.TerminalRenderCursorV2
import dev.termlink.terminal.api.TerminalRenderFrameV4
import dev.termlink.terminal.api.TerminalRenderFrameV5
import dev.termlink.terminal.api.TerminalRenderGlyphV5
import dev.termlink.terminal.api.TerminalRenderGraphemeV1
import dev.termlink.terminal.api.TerminalRenderImageProtocolV5
import dev.termlink.terminal.api.TerminalRenderImageV5
import dev.termlink.terminal.api.TerminalRenderPaletteV1
import dev.termlink.terminal.api.TerminalRenderRatioV5
import dev.termlink.terminal.api.TerminalRenderRenditionV3
import dev.termlink.terminal.api.TerminalRenderRowV1
import dev.termlink.terminal.api.TerminalRenderScreenV1
import dev.termlink.terminal.api.TerminalRenderSourceV5
import dev.termlink.terminal.api.TerminalRenderUnderlineStyleV3
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalShellCommand
import dev.termlink.terminal.api.TerminalShellPoint
import dev.termlink.terminal.api.TerminalShellState
import dev.termlink.terminal.api.TerminalWidthProfile
import java.security.MessageDigest
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalSemanticProbeTest {
    @Test fun shellSummaryHashesNormalizedWindowsPathAndKeepsRecentAnchors() {
        val uri = "file:///C:/Work/%E4%B8%AD%E6%96%87%20%E7%A9%BA%E6%A0%BC/"
        val point = TerminalShellPoint(42, 12, 3, 1, false)
        val commands = (1L..10L).map { id ->
            TerminalShellCommand(id, 3, point, point.copy(sequence = 43),
                point.copy(sequence = 44), point.copy(sequence = 45),
                if (id == 10L) 1 else 0, uri)
        }
        val summary = TerminalSemanticProbe.summarizeShell(TerminalShellState(uri, commands, 11))
        val observed = summary.getValue("commands").jsonArray
        val last = observed.last().jsonObject

        assertEquals("10", summary.getValue("command_count").jsonPrimitive.content)
        assertEquals("11", summary.getValue("next_command_id").jsonPrimitive.content)
        assertEquals(8, observed.size)
        assertEquals("3", observed.first().jsonObject.getValue("id").jsonPrimitive.content)
        assertEquals("10", last.getValue("id").jsonPrimitive.content)
        assertEquals("3", last.getValue("phase").jsonPrimitive.content)
        assertEquals("1", last.getValue("exit_code").jsonPrimitive.content)
        assertEquals(digest("c:\\work\\中文 空格".toByteArray()),
            summary.getValue("cwd_path_sha256").jsonPrimitive.content)
        assertEquals(summary.getValue("cwd_path_sha256").jsonPrimitive.content,
            last.getValue("cwd_path_sha256").jsonPrimitive.content)
        assertEquals("42", last.getValue("prompt").jsonObject.getValue("sequence").jsonPrimitive.content)
        assertEquals("43", last.getValue("input").jsonObject.getValue("sequence").jsonPrimitive.content)
        assertEquals("44", last.getValue("output").jsonObject.getValue("sequence").jsonPrimitive.content)
        assertEquals("45", last.getValue("end").jsonObject.getValue("sequence").jsonPrimitive.content)
        assertEquals("12", last.getValue("end").jsonObject.getValue("line_id").jsonPrimitive.content)
        assertEquals("3", last.getValue("end").jsonObject.getValue("cell_offset").jsonPrimitive.content)
        assertEquals("1", last.getValue("end").jsonObject.getValue("page").jsonPrimitive.content)
        assertEquals("false", last.getValue("end").jsonObject.getValue("alternate").jsonPrimitive.content)
        assertFalse(summary.toString().contains("file:///"))
        assertFalse(summary.toString().contains("中文 空格"))
        assertFalse(summary.toString().contains("C:/Work"))
        assertTrue(summary.toString().toByteArray().size < 32 * 1024)
        val invalid = TerminalSemanticProbe.summarizeShell(TerminalShellState("file://remote/C:/private", emptyList(), 1))
        assertEquals("null", invalid.getValue("cwd_path_sha256").toString())
    }

    @Test fun textFrameIsBoundedAndDoesNotReturnPlainText() {
        val frame = frame(65, 13, "X", title = "private title")
        val summary = TerminalSemanticProbe.summarize(frame)

        assertEquals("65", summary.getValue("columns").jsonPrimitive.content)
        assertEquals("13", summary.getValue("rows").jsonPrimitive.content)
        assertEquals("60", summary.getValue("observed_columns").jsonPrimitive.content)
        assertEquals("12", summary.getValue("observed_rows").jsonPrimitive.content)
        assertEquals(12, summary.getValue("screen_rows").jsonArray.size)
        assertEquals(60, summary.getValue("screen_rows").jsonArray[0].jsonObject.getValue("cells").jsonArray.size)
        assertEquals(digest("X".toByteArray()), summary.getValue("screen_rows").jsonArray[0].jsonObject
            .getValue("cells").jsonArray[0].jsonObject.getValue("grapheme_sha256").jsonPrimitive.content)
        assertEquals(digest("private title".toByteArray()), summary.getValue("title_sha256").jsonPrimitive.content)
        assertFalse(summary.toString().contains("private title"))
        assertFalse(summary.toString().contains("\"grapheme\":\"X\""))
    }

    @Test fun graphicsSummaryUsesRealPixelsAndVisibleReferences() {
        val text = frame(2, 1, "A")
        val alpha = TerminalGraphicsResource(1, 1, 1, TerminalGraphicsFormat.A8, byteArrayOf(255.toByte()))
        val red = TerminalGraphicsResource(2, 1, 1, TerminalGraphicsFormat.RGBA8888,
            byteArrayOf(255.toByte(), 0, 0, 255.toByte()))
        val image = TerminalRenderImageV5(1, 2, 1, 0, 0, 0,
            TerminalRenderSourceV5(0, 0, 1, 1), TerminalRenderRatioV5(0, 1), TerminalRenderRatioV5(0, 1),
            TerminalRenderRatioV5(1, 1), TerminalRenderRatioV5(1, 1), 1, 1,
            TerminalRenderImageProtocolV5.SIXEL, TerminalRenderClipV5(0, 0, 1, 1))
        val frame = TerminalRenderFrameV5(text, TerminalGraphicsResources(3, listOf(alpha, red)),
            (1L..5L).map { image.copy(placementId = it) }, listOf(TerminalRenderGlyphV5(0, 0, 1, 0, 1, 1)))
        val summary = TerminalSemanticProbe.summarize(frame)

        assertEquals("5", summary.getValue("image_count").jsonPrimitive.content)
        assertEquals(4, summary.getValue("images").jsonArray.size)
        assertEquals("1", summary.getValue("glyph_count").jsonPrimitive.content)
        assertEquals("SIXEL", summary.getValue("images").jsonArray[0].jsonObject.getValue("protocol").jsonPrimitive.content)
        val resources = summary.getValue("resources").jsonArray.map { it.jsonObject }
        assertEquals(2, resources.size)
        assertEquals(digest(byteArrayOf(255.toByte())), resources[0].getValue("pixels_sha256").jsonPrimitive.content)
        assertEquals(digest(byteArrayOf(255.toByte(), 0, 0, 255.toByte())),
            resources[1].getValue("pixels_sha256").jsonPrimitive.content)
        assertTrue(summary.getValue("glyphs").jsonArray.isNotEmpty())
    }

    private fun frame(columns: Int, rows: Int, grapheme: String, title: String = ""): TerminalRenderFrameV4 =
        TerminalRenderFrameV4(TerminalWidthProfile.UNICODE_16, 3, 3, TerminalSize(columns, rows),
            TerminalRenderScreenV1.PRIMARY, false,
            TerminalRenderCursorV2(0, 0, true, false, TerminalRenderCursorShapeV2.BLOCK, false),
            listOf(TerminalRenderGraphemeV1(grapheme, 1)),
            listOf(TerminalRenderRenditionV3(TerminalRenderColorV1.Indexed(1), TerminalRenderColorV1.Default,
                TerminalRenderColorV1.Default, faint = true, underlineStyle = TerminalRenderUnderlineStyleV3.CURLY)),
            TerminalRenderPaletteV1(List(256) { null }, null, null, null, selectionBackgroundRgb = 0x123456),
            emptyList(), List(rows) {
                TerminalRenderRowV1(columns, false,
                    List(columns) { TerminalRenderCellV1(1, 0, false, false, 0) }, TerminalLineRendition.SINGLE_WIDTH)
            }, title)

    private fun digest(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes)
        .joinToString("") { "%02x".format(it.toInt() and 0xff) }
}

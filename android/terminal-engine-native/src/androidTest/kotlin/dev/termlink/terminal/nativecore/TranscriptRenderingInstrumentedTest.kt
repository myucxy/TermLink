package dev.termlink.terminal.nativecore

import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.view.View
import android.view.inputmethod.EditorInfo
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.termlink.terminal.api.*
import java.io.File
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class TranscriptRenderingInstrumentedTest {
    @Test
    fun realAndroidCanvasRendersStylesAndHistoryNeverCreatesInputConnection() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val activity = instrumentation.startActivitySync(
            Intent(instrumentation.context, ViewportTestActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        )
        instrumentation.runOnMainSync {
            try {
                val theme = NativeTerminalEngine.DEFAULT_THEME
                val styles = (0..5).map { index ->
                    TerminalStyleSnapshotV1(
                        TerminalColorSnapshotV1.Palette(index), TerminalColorSnapshotV1.Default,
                        attributes = TerminalCellAttributesSnapshotV1(
                            bold = index == 1, italic = index == 2, underline = index == 3,
                            dim = index == 4, inverse = index == 5,
                        ),
                        underlineStyle = if (index == 3) TerminalUnderlineStyleSnapshotV1.SINGLE else TerminalUnderlineStyleSnapshotV1.NONE,
                    )
                }
                val rows = (1L..60L).map { id ->
                    val style = (id % 6).toInt()
                    val cells = "Row$id Aa+-= ".map { TerminalCellSnapshotV1(it.toString(), 1, style) }.toMutableList()
                    cells += TerminalCellSnapshotV1("中", 2, style)
                    cells += TerminalCellSnapshotV1("", 0, style, true)
                    cells += TerminalCellSnapshotV1("e\u0301", 1, style)
                    while (cells.size < 80) cells += TerminalCellSnapshotV1(" ", 1, style)
                    TerminalRowSnapshotV1(cells, lineId = id, sealedAtSequence = 90)
                }
                val page = TerminalTranscriptPageV1(snapshotAnchorSequence = 100, firstLineId = 1,
                    lastLineId = 60, hasMoreBefore = false, nextBeforeLineId = 0, columns = 80,
                    rows = rows, styleTable = styles, palette = TerminalPaletteSnapshotV1(List(256) { it * 0x010101 }, 0xffffff, 0, 0xffffff))
                for (family in listOf(TerminalFontFamilyHintV1.SYSTEM_MONOSPACE, TerminalFontFamilyHintV1.BUNDLED_NERD_FONT))
                for (font in listOf(14f, 23f)) for (spacing in listOf(0.8f, 1.4f)) {
                    val selectedTheme = theme.copy(fontFamilyHint = family)
                    val native = NativeTranscriptViewport()
                    try {
                        native.applyTheme(selectedTheme)
                        native.replacePages(listOf(page), 80)
                        val view = native.createView(activity, font, spacing, {}, {}) as NativeTerminalView
                        activity.setContentView(view)
                        fun render(target: View): Bitmap {
                            target.measure(View.MeasureSpec.makeMeasureSpec(640, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(400, View.MeasureSpec.EXACTLY))
                            target.layout(0, 0, 640, 400)
                            return Bitmap.createBitmap(640, 400, Bitmap.Config.ARGB_8888).also {
                                val canvas = Canvas(it)
                                canvas.drawColor(theme.background.value)
                                target.draw(canvas)
                            }
                        }
                        val actual = render(view)
                        assertEquals(80, native.debugState().columns)
                        assertTrue(native.debugState().visibleText.endsWith("中e\u0301"))
                        val pixels = IntArray(actual.width * actual.height)
                        actual.getPixels(pixels, 0, actual.width, 0, 0, actual.width, actual.height)
                        assertTrue("Android Canvas must paint styled glyphs", pixels.toSet().size > 8)
                        assertNull(view.onCreateInputConnection(EditorInfo()))
                        assertFalse(view.onCheckIsTextEditor())
                        val directory = File(instrumentation.context.filesDir, "transcript-rendering").apply { mkdirs() }
                        val label = "${family.substringAfter(':')}-$font-$spacing"
                        File(directory, "native-$label.png").outputStream().use { actual.compress(Bitmap.CompressFormat.PNG, 100, it) }
                        assertTrue(native.scrollToLineId(10))
                        assertEquals(10L, native.debugState().firstVisibleLineId)
                    } finally {
                        native.close()
                    }
                }
            } finally { activity.finish() }
        }
    }
}

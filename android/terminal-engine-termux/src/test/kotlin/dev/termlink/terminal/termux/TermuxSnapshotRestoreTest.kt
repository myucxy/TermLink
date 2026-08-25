package dev.termlink.terminal.termux

import dev.termlink.terminal.api.TerminalCellSnapshotV1
import dev.termlink.terminal.api.TerminalColorSnapshotV1
import dev.termlink.terminal.api.TerminalCursorSnapshotV1
import dev.termlink.terminal.api.TerminalModesSnapshotV1
import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalRowSnapshotV1
import dev.termlink.terminal.api.TerminalScreenKindV1
import dev.termlink.terminal.api.TerminalScreenSnapshotV1
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalSnapshotV1
import dev.termlink.terminal.api.TerminalStyleSnapshotV1
import kotlinx.coroutines.test.runTest
import org.junit.Test
import java.nio.ByteBuffer
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class TermuxSnapshotRestoreTest {
    @Test
    fun prepareIsDetachedUntilExplicitInstall() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(COLUMNS, ROWS))
        engine.feed(1, ByteBuffer.wrap("OLD".encodeToByteArray()))
        val before = engine.visibleText()

        val prepared = engine.prepareSnapshot(snapshot(sequence = 10, firstRow = "TARGET"))

        assertEquals(before, engine.visibleText())
        val installed = engine.installPrepared(prepared)
        assertEquals(10, installed.sequence)
        assertEquals(TerminalSize(COLUMNS, ROWS), installed.size)
        assertContains(engine.visibleText(), "TARGET")
        engine.dispose()
    }

    @Test
    fun rgbForegroundAndBackgroundSnapshotRestores() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(COLUMNS, ROWS))
        val rgbStyle = TerminalStyleSnapshotV1(
            foreground = TerminalColorSnapshotV1.Rgb(0x12_34_56),
            background = TerminalColorSnapshotV1.Rgb(0xab_cd_ef),
        )

        val prepared = engine.prepareSnapshot(
            snapshot(sequence = 10, firstRow = "RGB", style = rgbStyle),
        )
        val installed = engine.installPrepared(prepared)

        assertEquals(10, installed.sequence)
        assertContains(engine.visibleText(), "RGB")
        engine.dispose()
    }

    @Test
    fun invalidPrepareLeavesInstalledRendererUntouched() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(COLUMNS, ROWS))
        engine.feed(1, ByteBuffer.wrap("KEEP".encodeToByteArray()))
        val before = engine.visibleText()
        val invalid = snapshot(sequence = 10, firstRow = "TARGET").copy(
            primaryScreen = screen("TARGET").copy(visibleRows = listOf(row("TARGET"))),
        )

        assertFailsWith<IllegalArgumentException> { engine.prepareSnapshot(invalid) }

        assertEquals(before, engine.visibleText())
        engine.dispose()
    }

    @Test
    fun installRejectsPreparedCheckpointAfterRendererAdvances() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(COLUMNS, ROWS))
        val prepared = engine.prepareSnapshot(snapshot(sequence = 10, firstRow = "TARGET"))
        engine.feed(1, ByteBuffer.wrap("NEW".encodeToByteArray()))
        val advanced = engine.visibleText()

        assertFailsWith<IllegalStateException> { engine.installPrepared(prepared) }

        assertEquals(advanced, engine.visibleText())
        engine.dispose()
    }

    @Test
    fun preparedCheckpointCannotCrossEngineBoundary() = runTest {
        val source = TermuxTerminalEngine(TerminalSize(COLUMNS, ROWS))
        val target = TermuxTerminalEngine(TerminalSize(COLUMNS, ROWS))
        target.feed(1, ByteBuffer.wrap("KEEP".encodeToByteArray()))
        val before = target.visibleText()
        val prepared = source.prepareSnapshot(snapshot(sequence = 10, firstRow = "TARGET"))

        assertFailsWith<IllegalArgumentException> { target.installPrepared(prepared) }

        assertEquals(before, target.visibleText())
        source.dispose()
        target.dispose()
    }

    private fun snapshot(
        sequence: Long,
        firstRow: String,
        style: TerminalStyleSnapshotV1 = TerminalStyleSnapshotV1(
            foreground = TerminalColorSnapshotV1.Default,
            background = TerminalColorSnapshotV1.Default,
        ),
    ): TerminalSnapshotV1 {
        val value = screen(firstRow)
        return TerminalSnapshotV1(
            sequence = sequence,
            size = TerminalSize(COLUMNS, ROWS),
            activeScreen = TerminalScreenKindV1.PRIMARY,
            primaryScreen = value,
            alternateScreen = value,
            modes = TerminalModesSnapshotV1(),
            palette = TerminalPaletteSnapshotV1(
                colorsRgb = List(256) { 0 },
                defaultForegroundRgb = 0x00ff_ffff,
                defaultBackgroundRgb = 0,
                defaultCursorRgb = 0x00ff_ffff,
            ),
            tabStops = emptyList(),
            styleTable = listOf(style),
        )
    }

    private fun screen(firstRow: String): TerminalScreenSnapshotV1 {
        val cursor = TerminalCursorSnapshotV1(
            column = 0,
            row = 1,
            renditionStyleIndex = 0,
        )
        return TerminalScreenSnapshotV1(
            visibleRows = listOf(row(firstRow), row("")),
            cursor = cursor,
            savedCursor = cursor,
            scrollTop = 0,
            scrollBottomExclusive = ROWS,
            marginLeft = 0,
            marginRightExclusive = COLUMNS,
            currentStyleIndex = 0,
        )
    }

    private fun row(text: String): TerminalRowSnapshotV1 {
        require(text.length <= COLUMNS)
        return TerminalRowSnapshotV1(
            cells = text.padEnd(COLUMNS).map { character ->
                TerminalCellSnapshotV1(
                    grapheme = character.toString(),
                    displayWidth = 1,
                    styleIndex = 0,
                )
            },
        )
    }

    private companion object {
        const val COLUMNS = 6
        const val ROWS = 2
    }
}

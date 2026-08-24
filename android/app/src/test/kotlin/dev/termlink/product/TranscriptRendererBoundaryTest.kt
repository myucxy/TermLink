package dev.termlink.product

import dev.termlink.terminal.api.TerminalPaletteSnapshotV1
import dev.termlink.terminal.api.TerminalTranscriptPageV1
import dev.termlink.terminal.termux.TermuxTranscriptViewport
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class TranscriptRendererBoundaryTest {
    @Test
    fun malformedMappedPageBecomesAStableErrorInsteadOfEscapingTheUiEffect() {
        val viewport = TermuxTranscriptViewport()
        val malformed = TerminalTranscriptPageV1(
            snapshotAnchorSequence = 80,
            firstLineId = 41,
            lastLineId = 60,
            hasMoreBefore = false,
            nextBeforeLineId = 0,
            columns = 120,
            rows = emptyList(),
            styleTable = emptyList(),
            palette = TerminalPaletteSnapshotV1(emptyList(), 0, 0, 0),
        )

        val error = applyTranscriptViewportPagesSafely(viewport, listOf(malformed), 120)

        assertNotNull(error)
        assertEquals(0, viewport.debugState().rowCount)
        // A rejected projection is atomic: the last valid blank renderer remains usable.
        assertEquals(80, viewport.debugState().columns)
        viewport.close()
    }
}

package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalRenderFrameV5
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertIs

class GraphicsHistoryTest {
    @Test
    fun versionTwoStrictlyDecodesFrozenV5FramesAndRetainsText() {
        val page = NativeHistoryPageDecoder.decodeV2(historyPage(), 9)
        assertEquals(7L, page.revision)
        assertEquals(2, page.cells)
        assertEquals(10L, page.nextBeforeLineId)
        assertEquals(1, page.lines.size)
        assertIs<TerminalRenderFrameV5>(page.frames.single())
        assertEquals(" ", page.frames.single().graphemes.single().text)
    }

    @Test
    fun historyVersionsAndEmbeddedFrameVersionsNeverDowngrade() {
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decode(historyPage(), 9) }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(historyPage(version = 1, frame = graphicsTextFrame()), 9) }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(historyPage(frame = graphicsTextFrame()), 9) }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decode(historyPage(version = 1), 9) }
        assertEquals(4, NativeHistoryPageDecoder.decode(historyPage(version = 1, frame = graphicsTextFrame()), 9).frames.single().formatVersion)
    }

    @Test
    fun rejectsUnknownFeaturesReservedBytesCrcOwnerAndFrozenRevision() {
        for (offset in listOf(72, 76, 80, 88)) {
            val bytes = historyPage().also { graphicsPut32(it, offset, 2); graphicsSeal(it, 92) }
            assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(bytes, 9) }
        }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(historyPage().also { it[92] = 0 }, 9) }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(historyPage(), 10) }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(historyPage(frame = graphicsFrame(8)), 9) }
        assertFailsWith<IllegalArgumentException> { NativeHistoryPageDecoder.decodeV2(historyPage().copyOf(96), 9) }
    }

    @Test
    fun graphicsCaptureUsesOnlyV2PagesAndViewportsAndReleasesOwnership() {
        var pages = 0
        var viewports = 0
        var releases = 0
        val info = NativeHistoryCaptureInfo(9, 10, 7, 1024, 300_000)
        val bridge = object : NativeBridge by FakeNativeBridge() {
            override fun historyPageV2(info: NativeHistoryCaptureInfo, beforeLineId: Long, maxLines: Int, maxCells: Int, maxUtf8Bytes: Int): NativeBridgeResult<NativeHistoryRead> {
                pages++
                return NativeBridgeResult.Success(NativeHistoryRead.Page(historyPage()))
            }
            override fun historyViewportV2(info: NativeHistoryCaptureInfo, offsetRows: Long): NativeBridgeResult<NativeBridgeScrollbackFrame> {
                viewports++
                return NativeBridgeResult.Success(NativeBridgeScrollbackFrame(graphicsFrame(), 3, 4, offsetRows.coerceAtMost(3)))
            }
            override fun historyRelease(info: NativeHistoryCaptureInfo): NativeBridgeResult<Unit> {
                releases++
                return NativeBridgeResult.Success(Unit)
            }
        }
        val capture = NativeHistoryCapture(bridge, info, graphicsProfile = true)
        capture.use {
            assertIs<TerminalRenderFrameV5>(it.read().frames.single())
            assertIs<TerminalRenderFrameV5>(it.viewport(20).renderFrame)
            assertEquals(3L, it.viewport(20).appliedOffsetRows)
        }
        capture.close()
        assertEquals(1, pages)
        assertEquals(2, viewports)
        assertEquals(1, releases)
        assertFailsWith<IllegalStateException> { capture.read() }
    }

    @Test
    fun budgetAndWrongViewportVersionCannotExposePartialGraphicsHistory() {
        val info = NativeHistoryCaptureInfo(9, 10, 7, 1024, 300_000)
        val bridge = object : NativeBridge by FakeNativeBridge() {
            override fun historyPageV2(info: NativeHistoryCaptureInfo, beforeLineId: Long, maxLines: Int, maxCells: Int, maxUtf8Bytes: Int): NativeBridgeResult<NativeHistoryRead> =
                NativeBridgeResult.Success(NativeHistoryRead.Budget(2, 1))
            override fun historyViewportV2(info: NativeHistoryCaptureInfo, offsetRows: Long): NativeBridgeResult<NativeBridgeScrollbackFrame> =
                NativeBridgeResult.Success(NativeBridgeScrollbackFrame(graphicsTextFrame(), 0, 0, 0))
            override fun historyRelease(info: NativeHistoryCaptureInfo): NativeBridgeResult<Unit> = NativeBridgeResult.Success(Unit)
        }
        NativeHistoryCapture(bridge, info, graphicsProfile = true).use {
            assertEquals(2L, assertFailsWith<NativeHistoryBudgetException> { it.read() }.requiredCells)
            assertFailsWith<RenderFrameV5DecodeException> { it.viewport(0) }
        }
    }

    @Test
    fun versionTwoHasItsOwnFrozenSixtyFourMiBLimit() {
        assertEquals(16 * 1024 * 1024, NativeHistoryPageDecoder.MAX_BYTES)
        assertEquals(64 * 1024 * 1024, NativeHistoryPageDecoder.MAX_V2_BYTES)
        assertFailsWith<IllegalArgumentException> {
            NativeHistoryPageDecoder.decodeV2(ByteArray(NativeHistoryPageDecoder.MAX_V2_BYTES + 1), 9)
        }
    }
}

private fun historyPage(version: Int = 2, frame: ByteArray = graphicsFrame()): ByteArray = ByteArray(96 + 40 + 4 + frame.size).also {
    "TLHPV$version\u0000\u0000".toByteArray().copyInto(it)
    graphicsPut16(it, 8, version); graphicsPut16(it, 10, 96); graphicsPut32(it, 12, it.size)
    graphicsPut64(it, 16, 9); graphicsPut64(it, 24, 7); graphicsPut64(it, 32, -1); graphicsPut64(it, 40, 10)
    graphicsPut32(it, 48, 1); graphicsPut32(it, 52, 1); graphicsPut32(it, 56, 2); graphicsPut32(it, 60, 2)
    graphicsPut32(it, 72, if (version == 2) 1 else 0)
    graphicsPut64(it, 96, 10); graphicsPut64(it, 104, 7); graphicsPut32(it, 116, 1)
    graphicsPut32(it, 136, frame.size); frame.copyInto(it, 140); graphicsSeal(it, 92)
}

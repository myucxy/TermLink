package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize
import java.nio.ByteBuffer
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeTerminalEngineTest {
    @Test
    fun localHistoryScrollAnchorsAcrossOutputAndInputReturnsToLiveTail() = runBlocking {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            assertEquals(2_000L, bridge.lastConfigV3?.historyCapacityRows)
            bridge.forcedScrollbackAvailableRows = 5
            bridge.forcedScrollbackGenerationRows = 5
            engine.feed(1, ByteBuffer.wrap(byteArrayOf(1)))

            assertEquals(NativeHistoryScrollResult.MOVED, engine.scrollHistoryRows(3))
            assertEquals(3L, engine.historyOffsetRowsForTest())

            bridge.forcedScrollbackAvailableRows = 7
            bridge.forcedScrollbackGenerationRows = 7
            engine.feed(2, ByteBuffer.wrap(byteArrayOf(2)))
            assertEquals(5L, engine.historyOffsetRowsForTest())

            assertEquals(NativeHistoryScrollResult.MOVED, engine.scrollHistoryRows(-2))
            assertEquals(3L, engine.historyOffsetRowsForTest())
            engine.setReadOnly(false)
            assertTrue(engine.submitText("x"))
            assertEquals(0L, engine.historyOffsetRowsForTest())
            assertEquals(
                NativeHistoryScrollResult.LIVE_BOUNDARY,
                engine.scrollHistoryRows(-1),
            )
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun temporaryPresentationBoundaryDoesNotCrashHistoryNavigationOrAcceptedInput() = runBlocking {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            bridge.forcedScrollbackAvailableRows = 4
            bridge.forcedScrollbackGenerationRows = 4
            engine.feed(1, ByteBuffer.wrap(byteArrayOf(1)))
            assertEquals(NativeHistoryScrollResult.MOVED, engine.scrollHistoryRows(2))

            bridge.forcedRenderFailure = NativeBridgeResult.Failure(
                NativeTerminalStatus.PRESENTATION_DEFERRED.code,
            )
            assertEquals(NativeHistoryScrollResult.UNCHANGED, engine.scrollHistoryRows(1))
            assertEquals(2L, engine.historyOffsetRowsForTest())

            engine.setReadOnly(false)
            assertTrue(engine.submitText("x"))
            assertEquals(0L, engine.historyOffsetRowsForTest())
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun temporaryPresentationStatesDoNotFailClosedAfterCommittedApply() = runBlocking {
        listOf(
            NativeTerminalStatus.SNAPSHOT_NOT_ELIGIBLE,
            NativeTerminalStatus.PRESENTATION_DEFERRED,
        ).forEach { deferredStatus ->
            val bridge = FakeNativeBridge()
            val engine = NativeTerminalEngine(
                initialSize = TerminalSize(80, 24),
                bridge = bridge,
            )
            try {
                val initialFrame = engine.frames.value
                bridge.forcedRenderFailure = NativeBridgeResult.Failure(
                    deferredStatus.code,
                    "presentation is temporarily unavailable",
                )

                engine.feed(1, ByteBuffer.wrap(byteArrayOf(1)))

                assertEquals(2L, bridge.onlyStateForTest().nextSourceSequence)
                assertEquals(initialFrame, engine.frames.value)

                bridge.forcedRenderFailure = null
                engine.feed(2, ByteBuffer.wrap(byteArrayOf(2)))

                assertEquals(3L, bridge.onlyStateForTest().nextSourceSequence)
                assertEquals(3L, engine.frames.value.nextSourceSequence)
            } finally {
                engine.dispose()
            }
        }
    }
}

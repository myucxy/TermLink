package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize
import java.nio.ByteBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
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
    fun cancelledNavigationCannotPublishAnOlderFrameAfterReturningLive() {
        val enteredRender = CountDownLatch(1)
        val releaseRender = CountDownLatch(1)
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 8
            forcedScrollbackGenerationRows = 8
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        val executor = Executors.newSingleThreadExecutor()
        try {
            bridge.beforeScrollbackRender = { offsetRows ->
                if (offsetRows > 0) {
                    enteredRender.countDown()
                    assertTrue(releaseRender.await(5, TimeUnit.SECONDS))
                }
            }
            val navigation = engine.beginHistoryNavigation()
            val staleRequest = executor.submit<NativeHistoryScrollResult> {
                engine.scrollHistoryRows(3, navigation)
            }
            assertTrue(enteredRender.await(5, TimeUnit.SECONDS))

            engine.beginHistoryNavigation()
            releaseRender.countDown()

            assertEquals(NativeHistoryScrollResult.CANCELLED, staleRequest.get(5, TimeUnit.SECONDS))
            assertEquals(0L, engine.historyOffsetRowsForTest())
        } finally {
            releaseRender.countDown()
            executor.shutdownNow()
            engine.dispose()
        }
    }

    @Test
    fun prefetchedHistoryFrameIsReusedByTheNextScrollStep() = runBlocking {
        val bridge = FakeNativeBridge().apply {
            forcedScrollbackAvailableRows = 4
            forcedScrollbackGenerationRows = 4
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            bridge.scrollbackRenderThreads.clear()
            val navigation = engine.beginHistoryNavigation()

            assertEquals(
                NativeHistoryScrollResult.MOVED,
                engine.scrollHistoryRows(1, navigation),
            )
            engine.prefetchHistoryRows(1, navigation)
            val rendersAfterPrefetch = bridge.scrollbackRenderThreads.size

            assertEquals(
                NativeHistoryScrollResult.MOVED,
                engine.scrollHistoryRows(1, navigation),
            )

            assertEquals(2L, engine.historyOffsetRowsForTest())
            assertEquals(rendersAfterPrefetch, bridge.scrollbackRenderThreads.size)
        } finally {
            engine.dispose()
        }
    }

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

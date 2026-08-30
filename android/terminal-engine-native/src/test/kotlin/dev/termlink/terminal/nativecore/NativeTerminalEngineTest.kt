package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize
import java.nio.ByteBuffer
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeTerminalEngineTest {
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

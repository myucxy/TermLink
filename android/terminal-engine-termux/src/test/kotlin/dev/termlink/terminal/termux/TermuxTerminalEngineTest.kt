package dev.termlink.terminal.termux

import android.view.KeyEvent
import com.termux.terminal.TerminalEmulator
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.replay.GoldenStreamBundle
import dev.termlink.terminal.replay.GoldenStreamReplayer
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.onEach
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeoutOrNull
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets
import kotlin.test.assertContains
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class TermuxTerminalEngineTest {
    @Test
    fun burstInputDoesNotDropBytesWhileCollectorIsBackpressured() = runTest {
        val engine = TermuxTerminalEngine()
        engine.setReadOnly(false)
        val eventCount = 128
        val received = async(start = CoroutineStart.UNDISPATCHED) {
            withTimeoutOrNull(1_000) {
                engine.inputEvents
                    .onEach { delay(1) }
                    .take(eventCount)
                    .toList()
            }
        }

        repeat(eventCount) { assertTrue(engine.submitText("x")) }

        assertEquals(eventCount, received.await()?.size)
        engine.dispose()
    }

    @Test
    fun remoteResizeDoesNotPublishAnOutboundViewSize() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(80, 24))
        val outboundSize = async(start = CoroutineStart.UNDISPATCHED) {
            withTimeoutOrNull(1) { engine.sizeEvents.take(1).toList().single() }
        }

        engine.resize(120, 30)

        assertEquals(TerminalSize(120, 30), engine.currentSize())
        assertEquals(null, outboundSize.await())
        engine.dispose()
    }

    @Test
    fun goldenPowerShellStreamBuildsRealTerminalGrid() = runTest {
        val engine = TermuxTerminalEngine()

        val report = GoldenStreamReplayer().replay(loadGoldenBundle(), engine)

        assertEquals(TerminalSize(100, 30), report.finalSize)
        assertEquals(TerminalSize(100, 30), engine.currentSize())
        assertContains(engine.visibleText(), "__OUTPUT_CAPTURE_OK__")
        assertContains(engine.visibleText(), "__INPUT_CAPTURED__:android-input-simulation")
        engine.dispose()
    }

    @Test
    fun splitUtf8AndCursorMovementAreParsedAsTerminalState() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(12, 4))
        val stream = "alpha\r\nbeta\r\n中文🙂".toByteArray(StandardCharsets.UTF_8)

        engine.feed(1, ByteBuffer.wrap(stream.copyOfRange(0, stream.size - 2)))
        engine.feed(2, ByteBuffer.wrap(stream.copyOfRange(stream.size - 2, stream.size)))
        engine.feed(3, ByteBuffer.wrap("\u001b[1;1HOK".toByteArray(StandardCharsets.UTF_8)))

        assertEquals("OKpha\nbeta\n中文🙂\n", engine.visibleText())
        engine.dispose()
    }

    @Test
    fun textControlAndSpecialKeysEmitExactTerminalBytes() = runTest {
        val engine = TermuxTerminalEngine()
        engine.setReadOnly(false)

        val text = captureInput(engine, 3) { engine.submitText("中🙂\n") }
        assertContentEquals("中".encodeToByteArray(), text[0])
        assertContentEquals("🙂".encodeToByteArray(), text[1])
        assertContentEquals(byteArrayOf('\r'.code.toByte()), text[2])

        val control = captureInput(engine, 1) {
            engine.submitCodePoint('c'.code, controlDown = true)
        }
        assertContentEquals(byteArrayOf(3), control.single())

        val alt = captureInput(engine, 1) {
            engine.submitCodePoint('b'.code, altDown = true)
        }
        assertContentEquals("\u001bb".encodeToByteArray(), alt.single())

        val normalCursor = captureInput(engine, 1) {
            engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP)
        }
        assertContentEquals("\u001b[A".encodeToByteArray(), normalCursor.single())

        engine.feed(1, ByteBuffer.wrap("\u001b[?1h".encodeToByteArray()))
        val applicationCursor = captureInput(engine, 1) {
            engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP)
        }
        assertContentEquals("\u001bOA".encodeToByteArray(), applicationCursor.single())
        engine.dispose()
    }

    @Test
    fun bracketedPasteSanitizesControlsAndNormalizesNewlines() = runTest {
        val engine = TermuxTerminalEngine()
        engine.setReadOnly(false)
        engine.feed(1, ByteBuffer.wrap("\u001b[?2004h".encodeToByteArray()))

        val output = captureInput(engine, 3) {
            engine.submitPaste("alpha\nbeta\u001bunsafe")
        }

        assertContentEquals("\u001b[200~".encodeToByteArray(), output[0])
        assertContentEquals("alpha\rbetaunsafe".encodeToByteArray(), output[1])
        assertContentEquals("\u001b[201~".encodeToByteArray(), output[2])
        engine.dispose()
    }

    @Test
    fun sgrMouseAndFocusModesEmitProtocolBytes() = runTest {
        val engine = TermuxTerminalEngine(TerminalSize(10, 10))
        engine.setReadOnly(false)
        assertFalse(engine.submitMouseEvent(TerminalEmulator.MOUSE_LEFT_BUTTON, 3, 4, true))
        assertFalse(engine.submitFocusChanged(true))

        engine.feed(
            1,
            ByteBuffer.wrap("\u001b[?1000h\u001b[?1006h\u001b[?1004h".encodeToByteArray()),
        )
        val mouse = captureInput(engine, 2) {
            assertTrue(
                engine.submitMouseEvent(TerminalEmulator.MOUSE_LEFT_BUTTON, 3, 4, true),
            )
            engine.submitMouseEvent(TerminalEmulator.MOUSE_LEFT_BUTTON, 3, 4, false)
        }
        assertContentEquals("\u001b[<0;3;4M".encodeToByteArray(), mouse[0])
        assertContentEquals("\u001b[<0;3;4m".encodeToByteArray(), mouse[1])

        val focus = captureInput(engine, 2) {
            assertTrue(engine.submitFocusChanged(true))
            engine.submitFocusChanged(false)
        }
        assertContentEquals("\u001b[I".encodeToByteArray(), focus[0])
        assertContentEquals("\u001b[O".encodeToByteArray(), focus[1])
        engine.dispose()
    }

    @Test
    fun readOnlyModeRejectsEveryProgrammaticInputPath() {
        val engine = TermuxTerminalEngine()

        assertFalse(engine.submitText("blocked"))
        assertFalse(engine.submitCodePoint('c'.code, controlDown = true))
        assertFalse(engine.submitKeyCode(KeyEvent.KEYCODE_ENTER))
        assertFalse(engine.submitPaste("blocked"))
        assertFalse(engine.submitMouseEvent(TerminalEmulator.MOUSE_LEFT_BUTTON, 1, 1, true))
        assertFalse(engine.submitFocusChanged(true))
        engine.dispose()
    }

    private fun loadGoldenBundle(): GoldenStreamBundle {
        val base = "terminal-golden-streams/smoke-powershell-v1"
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

    private suspend fun captureInput(
        engine: TermuxTerminalEngine,
        count: Int,
        action: () -> Boolean,
    ): List<ByteArray> = coroutineScope {
        val received = async(start = CoroutineStart.UNDISPATCHED) {
            engine.inputEvents.take(count).toList()
        }
        assertTrue(action())
        received.await()
    }
}

package dev.termlink.terminal.nativecore

import android.graphics.Canvas
import android.graphics.Paint
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.MotionEvent
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.termlink.terminal.api.TerminalKittyKey
import dev.termlink.terminal.api.TerminalKittyKeyInput
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalCellPosition
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMousePosition
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.nio.ByteBuffer
import java.util.Collections
import java.util.concurrent.CountDownLatch
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first

@RunWith(AndroidJUnit4::class)
class JniNativeBridgeInstrumentedTest {
    @Test
    fun deviceFontMetricsBatchAnAsciiTerminalRowIntoOneTextDraw() = kotlinx.coroutines.runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val engine = NativeTerminalEngine(TerminalSize(20, 2))
        try {
            engine.feed(1, ByteBuffer.wrap("TermLink".encodeToByteArray()))
            val canvas = TextRecordingCanvas()
            instrumentation.runOnMainSync {
                val view = NativeTerminalView(
                    context = instrumentation.targetContext,
                    engine = null,
                    textSizeSp = 14f,
                    lineSpacingMultiplier = 1f,
                )
                view.layout(0, 0, 500, 100)
                view.drawFrame(canvas, engine.frames.value, engine.theme())
            }

            assertTrue(
                "the device font should draw adjacent ASCII cells as one safe run: ${canvas.texts}",
                canvas.texts.any { it.startsWith("TermLink") },
            )
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun historyDragUsesTheWorkerAndLeavesTheInstrumentationThreadResponsive() =
        kotlinx.coroutines.runBlocking {
            val instrumentation = InstrumentationRegistry.getInstrumentation()
            val engine = NativeTerminalEngine(TerminalSize(40, 10))
            try {
                val output = buildString {
                    repeat(100) { index -> append("line-").append(index).append("\r\n") }
                }
                engine.feed(1, ByteBuffer.wrap(output.encodeToByteArray()))
                lateinit var view: NativeTerminalView

                instrumentation.runOnMainSync {
                    view = engine.createView(instrumentation.targetContext) as NativeTerminalView
                    view.layout(0, 0, 360, 240)
                    val down = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, 100f, 40f, 0)
                    val move = MotionEvent.obtain(0, 80, MotionEvent.ACTION_MOVE, 100f, 420f, 0)
                    val up = MotionEvent.obtain(0, 100, MotionEvent.ACTION_UP, 100f, 420f, 0)
                    try {
                        assertTrue(view.dispatchTouchEvent(down))
                        assertTrue(view.dispatchTouchEvent(move))
                        assertTrue(view.dispatchTouchEvent(up))
                    } finally {
                        down.recycle()
                        move.recycle()
                        up.recycle()
                    }
                }

                view.awaitHistoryRequestsForTest()

                assertTrue(engine.historyOffsetRowsForTest() > 0L)
                assertFalse(view.isFollowingCursorForTest())
                assertEquals("TermLink-NativeHistory", view.historyWorkerThreadNameForTest())
            } finally {
                engine.dispose()
            }
        }

    @Test
    fun liveCursorRemainsVisibleWhenAndroidShrinksTerminalViewport() = kotlinx.coroutines.runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val engine = NativeTerminalEngine(TerminalSize(80, 24))
        try {
            engine.feed(1, ByteBuffer.wrap(("\r\n".repeat(23) + "x".repeat(79)).encodeToByteArray()))
            assertEquals(23, engine.frames.value.cursor.row)
            assertEquals(79, engine.frames.value.cursor.column)

            instrumentation.runOnMainSync {
                val view = engine.createView(instrumentation.targetContext) as NativeTerminalView
                view.layout(0, 0, 1080, 1200)
                view.layout(0, 0, 360, 360)

                assertTrue(view.isFollowingCursorForTest())
                assertTrue("the complete cursor cell must remain in the resized viewport", view.isCursorFullyVisibleForTest())
                assertTrue("the viewport must move upward when its bottom is covered", view.viewportYForTest() > 0f)
                assertTrue("the viewport must move sideways when its right edge is covered", view.viewportXForTest() > 0f)
            }
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun nativeViewInputConnectionOwnsNonRecursiveEditableBuffer() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        lateinit var connection: BaseInputConnection
        instrumentation.runOnMainSync {
            val view = NativeTerminalView(
                context = instrumentation.targetContext,
                engine = null,
                textSizeSp = 14f,
                lineSpacingMultiplier = 1f,
            )
            connection = view.onCreateInputConnection(EditorInfo()) as BaseInputConnection
        }

        val editable = connection.editable
        assertNotNull(editable)
        assertTrue(connection.setComposingText("draft", 1))
        assertEquals("draft", editable.toString())
        assertTrue(connection.finishComposingText())
        assertEquals("", editable.toString())
    }

    @Test
    fun nativeCreateApplyRenderCheckpointImportAndDestroyRoundTrip() {
        assertEquals(TERMLINK_TERMINAL_ABI_VERSION, JniNativeBridge.abiVersion)
        val terminal = NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(8, 2),
            initialNextSourceSequence = 1,
        )
        terminal.use {
            val receipt = it.feed(1, "hello".encodeToByteArray())
            assertEquals(2, receipt.nextSourceSequence)
            assertEquals(2, it.render().nextSourceSequence)
            val checkpoint = it.exportCheckpoint()
            assertTrue(checkpoint.payloadByteCount > 0)

            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint.copyPayload()).use { restored ->
                assertEquals(2, restored.nextSourceSequence)
                assertEquals(2, restored.render().nextSourceSequence)
            }
        }
    }

    @Test
    fun opaqueTokensRejectForgeryStaleUseAndDoubleDestroy() {
        val allocation = LongArray(3)
        assertEquals(
            NativeTerminalStatus.OK.code.toLong(),
            JniNativeMethods.create(8, 2, 1, allocation).toLong(),
        )
        val token = allocation[0]
        assertTrue(token > 0)
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code.toLong(),
            JniNativeMethods.destroy(Long.MAX_VALUE).toLong(),
        )
        assertEquals(NativeTerminalStatus.OK.code.toLong(), JniNativeMethods.destroy(token).toLong())
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code.toLong(),
            JniNativeMethods.destroy(token).toLong(),
        )
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code.toLong(),
            JniNativeMethods.applyBatch(
                token,
                longArrayOf(1, 3, 0, 0, 1),
                arrayOfNulls(1),
            ).toLong(),
        )
    }

    @Test
    fun configV3HostEffectsAndSemanticInputUseTheSameOpaqueTerminal() {
        NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(8, 2),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8_WITH_STANDALONE_C1,
            historyCapacityRows = 16,
        ).use { terminal ->
            val kitty = terminal.encodeKittyKey(
                TerminalKittyKeyInput(TerminalKittyKey.Character('x'.code)),
            )
            assertTrue(kitty.isNotEmpty())
            val mouse = terminal.encodeMouse(
                TerminalMouseInput.Move(
                    position = TerminalMousePosition(TerminalCellPosition(0, 0)),
                ),
            )
            assertTrue("mouse reporting is disabled by default", mouse.isEmpty())

            val unsupported = runCatching {
                terminal.encodeWin32Key(0x41, 0x1e, 'a'.code, true, 0, 1)
            }.exceptionOrNull() as NativeTerminalException
            assertEquals(NativeTerminalStatus.UNSUPPORTED, unsupported.status)

            val hostEffects = terminal.feedWithHostEffectsV2(1, "hello".encodeToByteArray())
            assertEquals(2, hostEffects.formatVersion)
            assertTrue(hostEffects.effects.isEmpty())
            assertEquals(2, terminal.render().nextSourceSequence)
        }
    }

    @Test
    fun productionMirrorAcceptsCapabilityQueriesWithoutEchoingHostRepliesAsInput() =
        kotlinx.coroutines.runBlocking {
            val engine = NativeTerminalEngine(TerminalSize(80, 24))
            val unexpectedInput = async(start = CoroutineStart.UNDISPATCHED) {
                engine.inputEvents.first()
            }
            try {
                val queries = (
                    "\u001b[4\$p" +
                        "\u001b[?25\$p" +
                        "\u001bP\$qm\u001b\\" +
                        "\u001bP+q436f;524742\u001b\\" +
                        "\u001b[=7u\u001b[?u" +
                        "\u001b]10;?\u001b\\" +
                        "\u001b]11;?\u001b\\"
                    ).encodeToByteArray()

                engine.feed(1, ByteBuffer.wrap(queries))

                assertEquals(2, engine.frames.value.nextSourceSequence)
                assertFalse(
                    "a remote mirror must discard replies already emitted by SessionHost",
                    unexpectedInput.isCompleted,
                )
            } finally {
                unexpectedInput.cancelAndJoin()
                engine.dispose()
            }
        }

    @Test
    fun inputV1AndRenderFrameV3AreProductionNativeContracts() {
        NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(80, 24),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8,
            historyCapacityRows = 10_000,
        ).use { terminal ->
            terminal.feed(
                1,
                "\u001b[?1h\u001b[?1004h\u001b[?2004h\u001b]2;Native V3\u0007"
                    .encodeToByteArray(),
            )
            assertArrayEquals("\u001bOA".encodeToByteArray(), terminal.encodeKey(TerminalKey.ARROW_UP))
            assertArrayEquals(
                byteArrayOf(3),
                terminal.encodeCharacter('c'.code, TerminalInputModifiers(control = true)),
            )
            assertArrayEquals(
                "\u001b[200~paste\u001b[201~".encodeToByteArray(),
                terminal.encodePaste("paste".encodeToByteArray()),
            )
            assertArrayEquals("\u001b[I".encodeToByteArray(), terminal.encodeFocus(true))
            assertArrayEquals("\u001b[O".encodeToByteArray(), terminal.encodeFocus(false))

            val frame = terminal.renderV3().renderFrame
            assertEquals(2, frame.nextSourceSequence)
            assertEquals("Native V3", frame.title)
            assertEquals(80, frame.size.columns)
            assertEquals(24, frame.size.rows)
        }
    }

    @Test
    fun nativeScrollbackProjectionReturnsOlderRowsWithoutChangingLiveTail() {
        NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(8, 2),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8,
            historyCapacityRows = 4,
        ).use { terminal ->
            terminal.feed(1, "A\r\nB\r\nC".encodeToByteArray())

            val live = terminal.renderV3AtScrollback(0)
            assertEquals(1L, live.availableRows)
            assertEquals(1L, live.generationRows)
            assertEquals(0L, live.appliedOffsetRows)
            assertEquals("B", firstCellText(live.renderFrame))

            val history = terminal.renderV3AtScrollback(1)
            assertEquals(1L, history.appliedOffsetRows)
            assertEquals("A", firstCellText(history.renderFrame))
            assertTrue(!history.renderFrame.cursor.visible)
            assertEquals("B", firstCellText(terminal.renderV3AtScrollback(0).renderFrame))
        }
    }

    @Test
    fun productionEngineUsesNativeAuthorityForOrderedStateAndSemanticInput() = kotlinx.coroutines.runBlocking {
        val engine = NativeTerminalEngine(TerminalSize(8, 2))
        try {
            engine.feed(1, java.nio.ByteBuffer.wrap("hello".encodeToByteArray()))
            engine.advance(2)
            engine.resize(3, 10, 3)
            assertEquals(4, engine.frames.value.nextSourceSequence)
            assertEquals(TerminalSize(10, 3), engine.authoritativeSize.value)

            engine.setReadOnly(false)
            assertTrue(engine.submitCodePoint('c'.code, controlDown = true))
            val encoded = engine.inputEvents.first()
            assertArrayEquals(byteArrayOf(3), encoded)
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun concurrentDestroyPublishesExactlyOneSuccess() {
        val allocation = LongArray(3)
        assertEquals(
            NativeTerminalStatus.OK.code.toLong(),
            JniNativeMethods.create(8, 2, 1, allocation).toLong(),
        )
        val ready = CountDownLatch(2)
        val start = CountDownLatch(1)
        val statuses = Collections.synchronizedList(mutableListOf<Int>())
        val workers = List(2) {
            Thread {
                ready.countDown()
                start.await()
                statuses += JniNativeMethods.destroy(allocation[0])
            }.apply { start() }
        }
        ready.await()
        start.countDown()
        workers.forEach(Thread::join)

        assertEquals(
            listOf(NativeTerminalStatus.OK.code, NativeTerminalStatus.INVALID_ARGUMENT.code),
            statuses.sorted(),
        )
    }

    private fun firstCellText(frame: dev.termlink.terminal.api.TerminalRenderFrameV3): String {
        val graphemeId = frame.rows.first().cells.first().graphemeId
        return frame.graphemes[graphemeId - 1].text
    }

    private class TextRecordingCanvas : Canvas() {
        val texts = mutableListOf<String>()

        override fun drawText(text: String, x: Float, y: Float, paint: Paint) {
            texts += text
        }

        override fun drawRect(left: Float, top: Float, right: Float, bottom: Float, paint: Paint) = Unit
    }
}

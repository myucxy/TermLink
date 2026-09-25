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
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.withTimeoutOrNull

@RunWith(AndroidJUnit4::class)
class JniNativeBridgeInstrumentedTest {
    @Test
    fun coreMouseCapabilityTracksSplitModesCheckpointAndResetWithoutSendingProbeInput() = kotlinx.coroutines.runBlocking {
        val engine = NativeTerminalEngine(TerminalSize(80, 24))
        try {
            assertFalse(engine.mouseReporting.value)
            var sequence = 1L
            "\u001b[?1003;1006h".encodeToByteArray().forEach {
                engine.feed(sequence++, ByteBuffer.wrap(byteArrayOf(it)))
            }
            assertTrue(engine.mouseReporting.value)
            engine.feed(sequence++, ByteBuffer.wrap("\u001b[?1003l".encodeToByteArray()))
            assertFalse(engine.mouseReporting.value)
            val checkpoint = NativeTerminalHandle.create(JniNativeBridge, TerminalSize(80, 24), 1).use {
                it.feed(1, "\u001b[?1002;1016h".encodeToByteArray())
                it.exportCheckpoint().copyPayload()
            }
            engine.installCheckpoint(checkpoint)
            assertTrue(engine.mouseReporting.value)
            engine.clear()
            assertFalse(engine.mouseReporting.value)
            assertEquals(null, withTimeoutOrNull(100) { engine.inputEvents.first() })
        } finally { engine.dispose() }
    }

    @Test
    fun codexMouseModeRequiresFingerOptInAndKeyboardActionNeverSendsInput() = kotlinx.coroutines.runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val activity = instrumentation.startActivitySync(
            android.content.Intent(instrumentation.context, ViewportTestActivity::class.java)
                .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK),
        )
        val engine = NativeTerminalEngine(TerminalSize(80, 24))
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[?1003;1006h".encodeToByteArray()))
            engine.setReadOnly(false)
            var clicks = 0
            instrumentation.runOnMainSync {
                val view = engine.createView(activity) as NativeTerminalView
                activity.setContentView(view)
                view.layout(0, 0, 500, 500)
                view.setOnClickListener { clicks++ }
                fun tap() {
                    val now = android.os.SystemClock.uptimeMillis()
                    for ((action, time) in listOf(MotionEvent.ACTION_DOWN to now, MotionEvent.ACTION_UP to now + 20)) {
                        val event = MotionEvent.obtain(now, time, action, 16f, 16f, 0)
                        try { assertTrue(view.dispatchTouchEvent(event)) } finally { event.recycle() }
                    }
                }
                tap()
                assertEquals(1, clicks)
                engine.setTouchMouseEnabled(true)
                tap()
                tap()
                assertEquals(1, clicks)
                engine.setKeyboardVisible(true)
                engine.setKeyboardVisible(false)
                assertEquals(TerminalSize(80, 24), engine.authoritativeSize.value)
            }
            val reports = kotlinx.coroutines.withTimeout(1_000) { engine.inputEvents.take(4).toList() }
                .map { it.decodeToString() }
            assertTrue(reports[0].startsWith("\u001b[<0;") && reports[0].endsWith("M"))
            assertTrue(reports[1].endsWith("m"))
            assertEquals(reports.take(2), reports.drop(2))
            assertEquals(null, withTimeoutOrNull(100) { engine.inputEvents.first() })
        } finally {
            instrumentation.runOnMainSync { engine.dispose(); activity.finish() }
        }
    }

    @Test
    fun fingerWheelAndHoldDragUseRealCoreAndMouseResetRestoresLocalTouch() = kotlinx.coroutines.runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val activity = instrumentation.startActivitySync(android.content.Intent(instrumentation.context,
            ViewportTestActivity::class.java).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
        val engine = NativeTerminalEngine(TerminalSize(80, 24))
        lateinit var view: NativeTerminalView
        var down = 0L
        var clicks = 0
        fun event(action: Int, y: Float = 100f) = instrumentation.runOnMainSync {
            val now = android.os.SystemClock.uptimeMillis()
            if (action == MotionEvent.ACTION_DOWN) down = now
            val input = MotionEvent.obtain(down, now, action, 100f, y, 0)
            try { assertTrue(view.dispatchTouchEvent(input)) } finally { input.recycle() }
        }
        suspend fun drain(): List<String> {
            val reports = mutableListOf<String>()
            while (true) reports += (withTimeoutOrNull(80) { engine.inputEvents.first() } ?: break).decodeToString()
            return reports
        }
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[?1003;1006h".encodeToByteArray()))
            engine.setReadOnly(false)
            engine.setTouchMouseEnabled(true)
            instrumentation.runOnMainSync {
                view = engine.createView(activity) as NativeTerminalView
                activity.setContentView(view)
                view.layout(0, 0, 500, 500)
                view.setOnClickListener { clicks++ }
            }
            instrumentation.waitForIdleSync()
            event(MotionEvent.ACTION_DOWN)
            assertTrue(drain().isEmpty())
            event(MotionEvent.ACTION_MOVE, 320f)
            event(MotionEvent.ACTION_UP, 320f)
            val wheels = drain()
            assertTrue(wheels.isNotEmpty())
            assertTrue(wheels.all { it.startsWith("\u001b[<64;") && it.endsWith("M") })
            assertEquals(0, clicks)
            event(MotionEvent.ACTION_DOWN)
            android.os.SystemClock.sleep(android.view.ViewConfiguration.getLongPressTimeout().toLong() + 100)
            instrumentation.waitForIdleSync()
            event(MotionEvent.ACTION_MOVE, 150f)
            event(MotionEvent.ACTION_CANCEL, 150f)
            val drag = drain()
            assertEquals(3, drag.size)
            assertTrue(drag[0].startsWith("\u001b[<0;") && drag[0].endsWith("M"))
            assertTrue(drag[1].startsWith("\u001b[<32;"))
            assertTrue(drag[2].endsWith("m"))
            engine.feed(2, ByteBuffer.wrap("\u001b[?1003l".encodeToByteArray()))
            assertFalse(engine.mouseReporting.value)
            event(MotionEvent.ACTION_DOWN)
            event(MotionEvent.ACTION_UP)
            assertEquals(1, clicks)
            assertTrue(drain().isEmpty())
        } finally { instrumentation.runOnMainSync { engine.dispose(); activity.finish() } }
    }

    @Test
    fun alternateScrollHintSurvivesSplitInputAndRestoresCheckpoint() {
        val prefix = "MAIN".encodeToByteArray()
        val suffix = "\u001b[?1049h\u001b[?1007h\u001b[1;1H\u001b[JALT".encodeToByteArray()
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(8, 2), 1).use { terminal ->
            terminal.feedWithHostEffectsV2(1, prefix)
            val checkpoint = terminal.exportCheckpoint().copyPayload()
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint).use { restored ->
                suffix.forEachIndexed { index, byte ->
                    restored.feedWithHostEffectsV2(index + 2L, byteArrayOf(byte))
                }
                assertEquals("ALT", frameText(restored.renderV3().renderFrame).trim())
                val alternate = restored.exportCheckpoint().copyPayload()
                NativeTerminalHandle.importCheckpoint(JniNativeBridge, alternate).use { reopened ->
                    val leave = "\u001b[?1007l\u001b[?1049l".encodeToByteArray()
                    reopened.feedWithHostEffectsV2(suffix.size + 2L, leave)
                    assertEquals("MAIN", frameText(reopened.renderV3().renderFrame).trim())
                }
            }
        }
    }

    @Test
    fun measuredPhoneViewportRespectsFontScaleSpacingAndKeyboardArea() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val intent = android.content.Intent(instrumentation.context, ViewportTestActivity::class.java)
            .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        val activity = instrumentation.startActivitySync(intent)
        instrumentation.runOnMainSync {
            val engine = NativeTerminalEngine(TerminalSize(120, 40))
            try {
                fun measure(font: Float, spacing: Float, height: Int = 600, scale: Float = 1f): dev.termlink.terminal.api.TerminalViewportMetrics {
                    val configuration = android.content.res.Configuration(activity.resources.configuration).apply { fontScale = scale }
                    val context = activity.createConfigurationContext(configuration)
                    val view = engine.createView(context, font, spacing)
                    activity.setContentView(view)
                    view.layout(0, 0, 400, height)
                    return requireNotNull(engine.measuredViewport())
                }
                val normal = measure(13f, 1f)
                val large = measure(26f, 1f)
                val spaced = measure(13f, 1.5f)
                val keyboard = measure(13f, 1f, height = 300)
                val scaled = measure(13f, 1f, scale = 1.5f)
                assertTrue(large.size.columns < normal.size.columns && large.size.rows < normal.size.rows)
                assertEquals(normal.size.columns, spaced.size.columns)
                assertTrue(spaced.size.rows < normal.size.rows)
                assertEquals(normal.size.columns, keyboard.size.columns)
                assertTrue(keyboard.size.rows < normal.size.rows)
                assertTrue(scaled.size.columns < normal.size.columns && scaled.size.rows < normal.size.rows)
                assertEquals(TerminalSize(120, 40), engine.authoritativeSize.value)
            } finally {
                activity.setContentView(android.view.View(activity))
                engine.dispose()
                activity.finish()
            }
        }
    }

    @Test
    fun openConsoleStartupRetainsFaintAcrossJniAndCheckpoint() {
        val bytes = ("\u001b[1t\u001b[c\u001b[?1004h\u001b[?9001h" +
            "\u001b[2mF\u001b[22mN\u001b]10;?\u001b\\\u001b]11;?\u001b\\").encodeToByteArray()
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(8, 2), 1).use { terminal ->
            // Network chunks may split any escape sequence, including the startup hint.
            bytes.forEachIndexed { index, byte -> terminal.feed(index + 1L, byteArrayOf(byte)) }
            val frame = terminal.renderV3().renderFrame
            assertEquals("F", firstCellText(frame))
            assertTrue(frame.renditions[frame.rows[0].cells[0].renditionId].faint)
            assertFalse(frame.renditions[frame.rows[0].cells[1].renditionId].faint)
            val checkpoint = terminal.exportCheckpoint().copyPayload()
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint).use { restored ->
                val restoredFrame = restored.renderV3().renderFrame
                assertEquals(frame.nextSourceSequence, restoredFrame.nextSourceSequence)
                assertEquals(frame.renditions, restoredFrame.renditions)
                assertEquals(frame.rows.map { it.cells }, restoredFrame.rows.map { it.cells })
            }
        }
    }

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
    fun nativeViewInputConnectionFlushesCompositionAndDeletesBothDirections() = kotlinx.coroutines.runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val engine = NativeTerminalEngine(TerminalSize(20, 2))
        lateinit var connection: BaseInputConnection
        try {
            engine.setReadOnly(false)
            instrumentation.runOnMainSync {
                val view = NativeTerminalView(
                    context = instrumentation.targetContext,
                    engine = engine,
                    textSizeSp = 14f,
                    lineSpacingMultiplier = 1f,
                )
                connection = view.onCreateInputConnection(EditorInfo()) as BaseInputConnection
            }

            val editable = connection.editable
            assertNotNull(editable)

            // Deletion must also work immediately after connection, before any composition sets a cursor.
            instrumentation.runOnMainSync { assertTrue(connection.deleteSurroundingTextInCodePoints(1, 1)) }
            val initialDeletes = kotlinx.coroutines.withTimeout(1_000) { engine.inputEvents.take(2).toList() }
            assertArrayEquals(byteArrayOf(0x7f), initialDeletes[0])
            assertArrayEquals("\u001b[3~".encodeToByteArray(), initialDeletes[1])

            val singleCommit = async(start = CoroutineStart.UNDISPATCHED) { engine.inputEvents.first() }
            instrumentation.runOnMainSync {
                assertTrue(connection.setComposingText("1", 1))
                assertTrue(connection.commitText("1", 1))
            }
            assertArrayEquals("1".encodeToByteArray(), withTimeoutOrNull(5_000) { singleCommit.await() })
            assertEquals(null, withTimeoutOrNull(250) { engine.inputEvents.first() })

            val composing = async(start = CoroutineStart.UNDISPATCHED) { engine.inputEvents.take(5).toList() }
            instrumentation.runOnMainSync {
                assertTrue(connection.setComposingText("draft", 1))
                assertEquals("draft", editable.toString())
                assertTrue(connection.finishComposingText())
                assertEquals("", editable.toString())
            }
            val committed = withTimeoutOrNull(5_000) { composing.await() }
            assertNotNull("finishComposingText must submit the committed text", committed)
            assertArrayEquals("draft".encodeToByteArray(), committed!!.flatMap(ByteArray::toList).toByteArray())

            val deletes = async(start = CoroutineStart.UNDISPATCHED) {
                engine.inputEvents.take(3).toList()
            }
            instrumentation.runOnMainSync { assertTrue(connection.deleteSurroundingText(2, 1)) }
            val deleted = withTimeoutOrNull(5_000) { deletes.await() }
            assertNotNull("deleteSurroundingText must submit two backspaces and one forward delete", deleted)
            assertEquals(
                listOf(byteArrayOf(0x7f), byteArrayOf(0x7f), "\u001b[3~".encodeToByteArray())
                    .map(ByteArray::toList),
                deleted!!.map(ByteArray::toList),
            )

            val unexpected = async(start = CoroutineStart.UNDISPATCHED) { engine.inputEvents.first() }
            instrumentation.runOnMainSync { assertTrue(connection.deleteSurroundingText(0, 0)) }
            assertFalse(unexpected.isCompleted)
            unexpected.cancelAndJoin()
        } finally {
            engine.dispose()
        }
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
                        "\u001b[>q" +
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
    fun semanticInputTracksKittyProtocolAndRenderFrameV3IsNative() {
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

            terminal.feed(2, "\u001b[=31u".encodeToByteArray())
            assertArrayEquals("/".encodeToByteArray(), terminal.encodeCommittedText("/"))
            assertArrayEquals(
                "\u001b[99;5:1u".encodeToByteArray(),
                terminal.encodeCharacter('c'.code, TerminalInputModifiers(control = true)),
            )
            assertArrayEquals(
                "\u001b[47:63;2:1u".encodeToByteArray(),
                terminal.encodeCharacter(
                    '/'.code,
                    TerminalInputModifiers(shift = true),
                    shiftedCodePoint = '?'.code,
                ),
            )
            assertArrayEquals(
                "\u001b[13;1:1u".encodeToByteArray(),
                terminal.encodeKey(TerminalKey.ENTER),
            )
            assertArrayEquals(
                "\u001b[27;1:1u".encodeToByteArray(),
                terminal.encodeKey(TerminalKey.ESCAPE),
            )
            assertArrayEquals(
                "\u001b[1;1:1A".encodeToByteArray(),
                terminal.encodeKey(TerminalKey.ARROW_UP),
            )

            terminal.feed(3, "\u001b[<u".encodeToByteArray())
            assertArrayEquals(
                byteArrayOf(3),
                terminal.encodeCharacter('c'.code, TerminalInputModifiers(control = true)),
            )

            val frame = terminal.renderV3().renderFrame
            assertEquals(4, frame.nextSourceSequence)
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
    fun importedCheckpointRemainsVisibleAfterLifecycleAdvance() = kotlinx.coroutines.runBlocking {
        val checkpoint = NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(16, 3),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8,
            historyCapacityRows = 32,
        ).use { source ->
            source.feed(1, "checkpoint-visible".encodeToByteArray())
            for (sequence in 2L..45L) {
                source.advance(sequence, 0)
            }
            assertEquals(46L, source.nextSourceSequence)
            source.exportCheckpoint().copyPayload()
        }

        val engine = NativeTerminalEngine(TerminalSize(8, 2))
        try {
            val installed = engine.installCheckpoint(checkpoint)
            assertEquals(45L, installed.sequence)
            assertEquals("c", firstCellText(engine.frames.value))

            // SessionEnded is an ordered lifecycle event. Android intentionally projects it as
            // an advance because it must consume the source sequence without changing the screen.
            engine.advance(46)

            assertEquals(47L, engine.frames.value.nextSourceSequence)
            assertEquals("c", firstCellText(engine.frames.value))
        } finally {
            checkpoint.fill(0)
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

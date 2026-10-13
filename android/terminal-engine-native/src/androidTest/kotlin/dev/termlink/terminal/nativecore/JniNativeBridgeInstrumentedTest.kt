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
import dev.termlink.terminal.api.TerminalShellState
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
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.withTimeoutOrNull

@RunWith(AndroidJUnit4::class)
class JniNativeBridgeInstrumentedTest {
    @Test fun clipboardIsLiveOnlyAndNeverBecomesTerminalInput() = runBlocking {
        val delivered = mutableListOf<dev.termlink.terminal.api.TerminalHostEffectV2>()
        val engine = NativeTerminalEngine(TerminalSize(12,3), platformActionSink = object : dev.termlink.terminal.api.TerminalPlatformActionSink {
            override fun offer(effects: List<dev.termlink.terminal.api.TerminalHostEffectV2>) { delivered.addAll(effects) }
            override fun close() = Unit
        })
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b]52;c;5Lit5paHCg==\u001b\\".toByteArray()))
            assertTrue(delivered.isEmpty())
            assertEquals(null, withTimeoutOrNull(100) { engine.inputEvents.first() })
            engine.notifyLiveOutputApplied(1); engine.notifyLiveOutputApplied(1)
            assertEquals(1, delivered.size)
            assertEquals(dev.termlink.terminal.api.TerminalPlatformAction.ClipboardWrite("中文\n"),
                dev.termlink.terminal.api.TerminalPlatformAction.decode(delivered.single().copyPayload()))
        } finally { engine.dispose() }
    }
    @Test fun progressRestoresButBellAndNotesNeverBecomeMirrorInput() = runBlocking {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(12,3),1).use { terminal ->
            val effects = terminal.applyWithHostEffectsV2(NativeEventBatch.of(listOf(
                NativeSourceEvent.Output(1,"\u0007\u001b[7;32;1;0;25,~\u001b]9;4;2;75\u001b\\".toByteArray()),
            )))
            assertEquals(2,effects.effects.size)
            assertTrue(effects.effects.all { it.kind == dev.termlink.terminal.api.TerminalHostEffectKindV2.PLATFORM_ACTION })
            assertEquals(dev.termlink.terminal.api.TerminalHostSignals(2,75),terminal.hostSignals())
            val checkpoint = terminal.exportCheckpoint()
            NativeTerminalHandle.importCheckpoint(JniNativeBridge,checkpoint.copyPayload()).use { restored ->
                assertEquals(terminal.hostSignals(),restored.hostSignals())
            }
        }
        val delivered = mutableListOf<dev.termlink.terminal.api.TerminalHostEffectV2>()
        var sinkClosed = false
        val engine = NativeTerminalEngine(TerminalSize(12,3), platformActionSink = object : dev.termlink.terminal.api.TerminalPlatformActionSink {
            override fun offer(effects: List<dev.termlink.terminal.api.TerminalHostEffectV2>) { delivered.addAll(effects) }
            override fun close() { sinkClosed = true }
        })
        try {
            engine.feed(1,ByteBuffer.wrap("\u0007\u001b[7;32;1,~".toByteArray()))
            assertEquals(null,withTimeoutOrNull(100) { engine.inputEvents.first() })
            assertTrue(delivered.isEmpty())
            engine.notifyLiveOutputApplied(1)
            engine.notifyLiveOutputApplied(1)
            assertEquals(2,delivered.size)
            engine.feed(2,ByteBuffer.wrap("\u0007\u001b]9;4;2;75\u001b\\".toByteArray()))
            engine.advance(3)
            engine.notifyLiveOutputApplied(2)
            assertEquals(2,delivered.size)
            assertEquals(dev.termlink.terminal.api.TerminalHostSignals(2,75),engine.hostSignals.value)
        } finally { engine.dispose() }
        assertTrue(sinkClosed)
    }
    @Test fun shellCommandsCwdRestoreAndFrozenAnchorNavigation() = runBlocking {
        val engine = NativeTerminalEngine(TerminalSize(12, 3))
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b]7;file:///C:/work/%E4%B8%AD%20test\u001b\\\u001b]133;A\u001b\\PS> \u001b]133;B\u001b\\echo hello\r\n\u001b]133;C\u001b\\hello\r\n\u001b]133;D;7\u001b\\more\r\nmore\r\n".toByteArray()))
            val state = engine.shellState()
            assertEquals("C:\\work\\中 test", TerminalShellState.localWindowsDirectory(state.cwdUri))
            assertEquals(7, state.commands.single().exitCode)
            assertTrue(engine.showShellCommand(state.commands.single().id))
            val frozen = engine.visibleText()
            engine.feed(2, ByteBuffer.wrap("new output\r\n\u001b[3J".toByteArray()))
            assertEquals(frozen, engine.visibleText())
            assertTrue(engine.showShellCommand(state.commands.single().id))
            engine.scrollToLatest()
            assertFalse(engine.showShellCommand(state.commands.single().id))
            assertEquals(0L, engine.historyOffsetRowsForTest())
        } finally { engine.dispose() }
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(12, 3), 1).use { handle ->
            handle.apply(NativeEventBatch.of(listOf(NativeSourceEvent.Output(1, "\u001b]7;file:///C:/restore\u001b\\\u001b]133;A\u001b\\".toByteArray()))))
            val checkpoint = handle.exportCheckpoint()
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint.copyPayload()).use { restored ->
                assertEquals(handle.shellState(), restored.shellState())
            }
        }
    }
    @Test
    fun programResizeRequestsAreDiscardedByTheMirrorUntilHostConfirmation() = runBlocking {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(80, 24), 1).use { terminal ->
            val effects = terminal.applyWithHostEffectsV2(NativeEventBatch.of(listOf(
                NativeSourceEvent.Output(1, "\u001b[96\u0024|\u001b[25*|".encodeToByteArray()),
            )))
            assertEquals(2, effects.effects.size)
            assertTrue(effects.effects.all { it.kind == dev.termlink.terminal.api.TerminalHostEffectKindV2.RESIZE_REQUEST })
        }
        val engine = NativeTerminalEngine(initialSize = TerminalSize(80, 24))
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[96\u0024|\u001b[25*|".encodeToByteArray()))
            assertEquals(80, engine.frames.value.size.columns)
            assertEquals(24, engine.frames.value.size.rows)
            assertEquals(null, withTimeoutOrNull(100) { engine.inputEvents.first() })
            engine.resize(2, 96, 25)
            assertEquals(96, engine.frames.value.size.columns)
            assertEquals(25, engine.frames.value.size.rows)
        } finally { engine.dispose() }
    }
    @Test
    fun browsingViewportStaysFrozenWhileLiveMirrorAdvancesAndInputResumesLive() = runBlocking {
        val engine = NativeTerminalEngine(TerminalSize(8,3))
        try {
            engine.feed(1, ByteBuffer.wrap("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\n".toByteArray()))
            assertEquals(NativeHistoryScrollResult.MOVED,engine.scrollHistoryRows(1))
            val frozen = engine.frames.value
            assertFalse(frozen.cursor.visible)
            engine.feed(2, ByteBuffer.wrap("new\r\n\u001b[3J".toByteArray()))
            assertTrue(frozen === engine.frames.value)
            assertEquals(1L, engine.historyOffsetRowsForTest())
            assertEquals(NativeHistoryScrollResult.MOVED,engine.scrollHistoryRows(1))
            assertEquals(frozen.presentationRevision,engine.frames.value.presentationRevision)
            engine.resize(3, 6, 4)
            assertEquals(TerminalSize(8,3),engine.frames.value.size)
            assertEquals(TerminalSize(6,4),engine.authoritativeSize.value)
            engine.resumeViewportAfterLocalInput()
            assertEquals(0L,engine.historyOffsetRowsForTest())
            assertEquals(4L,engine.frames.value.presentationRevision)
        } finally { engine.dispose() }
    }
    @Test
    fun immutableHistoryPagesSurviveClearResizeAndReleaseSource() {
        val bridge = JniNativeBridge
        val allocation = (bridge.createV3(NativeTerminalConfigV3(columns = 8, rows = 3, initialSourceSequence = 1, outputProfile = 0, historyCapacityRows = 1000)) as NativeBridgeResult.Success).value
        fun output(sequence: Long, text: String) {
            val batch = NativeEventBatch.of(listOf(NativeSourceEvent.Output(sequence,text.toByteArray())))
            assertTrue(bridge.applyBatch(allocation.handle,batch) is NativeBridgeResult.Success)
        }
        output(1, "\u001b]8;;https://example.test/\u001b\\one\u001b]8;;\u001b\\\r\n\u001b#6界\r\nthree\r\nfour\r\nfive\r\nsix\r\n")
        val info = (bridge.historyBegin(allocation.handle) as NativeBridgeResult.Success).value
        NativeHistoryCapture(bridge, info).use { capture ->
            val original = capture.readBytes()
            val page = capture.read()
            assertTrue(page.lines.size > 1)
            assertTrue(page.frames.any { it.hyperlinks.isNotEmpty() })
            val latest = capture.read(maxLines=1)
            assertTrue(latest.hasMoreBefore)
            assertEquals(page.lines.size-1,capture.read(latest.nextBeforeLineId).lines.size)
            try { capture.read(maxCells=1); error("Budget ignored") }
            catch (error: NativeHistoryBudgetException) { assertEquals(8L,error.requiredCells) }
            output(2,"new\r\n\u001b[3J\u001b[?1049h")
            assertTrue(bridge.destroy(allocation.handle) is NativeBridgeResult.Success)
            assertArrayEquals(original,capture.readBytes())
            for (end in 0 until original.size) {
                try { NativeHistoryPageDecoder.decode(original.copyOf(end),info.captureId); error("Truncated page accepted") }
                catch (_: IllegalArgumentException) { }
            }
        }
        val released = bridge.historyPage(info,-1,128,65536,4*1024*1024) as NativeBridgeResult.Failure
        assertEquals(NativeTerminalStatus.HISTORY_STALE_CAPTURE.code,released.statusCode)
    }
    @Test
    fun hostThemeChangesRealPaletteAndSurvivesJniHistory() {
        fun theme(scheme: Int) = dev.termlink.terminal.api.TerminalHostThemeV1.decode(ByteArray(1044).also {
            it[0] = 1; it[4] = scheme.toByte()
            for (index in 0 until 259) {
                val component = if (index == 257 && scheme == 1) 0 else 255
                for (channel in 0..2) it[8 + index * 4 + channel] = component.toByte()
            }
        })
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(80, 24), 1).use { terminal ->
            val replies = terminal.applyWithHostEffectsV2(NativeEventBatch.of(listOf(
                NativeSourceEvent.HostTheme(1, theme(1)),
                NativeSourceEvent.Output(2, "\u001b[?2031h".encodeToByteArray()),
                NativeSourceEvent.HostTheme(3, theme(2)),
            ))).effects
            assertEquals(listOf("\u001b[?997;1n", "\u001b[?997;2n"), replies.map { it.copyPayload().decodeToString() })
            assertEquals(1044, replies.last().sourceOffsetEnd)
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                assertEquals("\u001b[?997;2n", copy.feedWithHostEffectsV2(4,
                    "\u001b[?996n".encodeToByteArray()).effects.single().copyPayload().decodeToString())
                assertEquals(0xffffff, copy.renderV4().renderFrame.palette.defaultBackgroundRgb)
            }
        }
    }

    @Test
    fun mirrorThemeActuallyPaintsPixelsAndNeverEmitsPtyReplies() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val engine = NativeTerminalEngine(TerminalSize(20, 5))
        try {
            val payload = ByteArray(1044).also { it[0] = 1; it[4] = 2
                for (channel in 0..2) it[8 + 257 * 4 + channel] = 255.toByte() }
            engine.applyHostTheme(1, dev.termlink.terminal.api.TerminalHostThemeV1.decode(payload))
            engine.feed(2, ByteBuffer.wrap("\u001b[?25l\u001b[?2031h\u001b[?996n".encodeToByteArray()))
            assertEquals(null, withTimeoutOrNull(100) { engine.inputEvents.first() })
            instrumentation.runOnMainSync {
                val view = NativeTerminalView(instrumentation.targetContext, engine, 14f, 1f)
                view.layout(0, 0, 900, 420)
                val bitmap = android.graphics.Bitmap.createBitmap(900, 420, android.graphics.Bitmap.Config.ARGB_8888)
                try {
                    view.drawFrame(Canvas(bitmap), engine.frames.value, engine.theme())
                    assertEquals(0xffffffff.toInt(), bitmap.getPixel(450, 400))
                } finally { bitmap.recycle() }
            }
            payload[4] = 1
            for (channel in 0..2) payload[8 + 257 * 4 + channel] = 0
            engine.applyHostTheme(3, dev.termlink.terminal.api.TerminalHostThemeV1.decode(payload))
            assertEquals(0, engine.frames.value.palette.defaultBackgroundRgb)
            assertEquals(null, withTimeoutOrNull(100) { engine.inputEvents.first() })
        } finally { engine.dispose() }
    }

    @Test
    fun orderedHostGeometrySurvivesJniSnapshotResizeAndWithdrawal() {
        val geometry = dev.termlink.terminal.api.TerminalHostGeometryV1(9, 18, 1920, 1080, 744, 476)
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(80,24), 1).use { terminal ->
            val replies = terminal.applyWithHostEffectsV2(NativeEventBatch.of(listOf(
                NativeSourceEvent.HostGeometry(1, geometry),
                NativeSourceEvent.Output(2, "\u001b[14t\u001b[14;2t\u001b[15t\u001b[16t\u001b[18t\u001b[19t".encodeToByteArray()),
            ))).effects.map { it.copyPayload().decodeToString() }
            assertEquals(listOf("\u001b[4;432;720t", "\u001b[4;476;744t", "\u001b[5;1080;1920t",
                "\u001b[6;18;9t", "\u001b[8;24;80t", "\u001b[9;60;213t"), replies)
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                val resized = copy.applyWithHostEffectsV2(NativeEventBatch.of(listOf(
                    NativeSourceEvent.Resize(3, TerminalSize(40, 10)),
                    NativeSourceEvent.Output(4, "\u001b[14t".encodeToByteArray()),
                ))).effects
                assertEquals("\u001b[4;180;360t", resized.single().copyPayload().decodeToString())
                copy.apply(NativeEventBatch.of(listOf(NativeSourceEvent.HostGeometry(5,
                    dev.termlink.terminal.api.TerminalHostGeometryV1()))))
                assertTrue(copy.feedWithHostEffectsV2(6, "\u001b[16t".encodeToByteArray()).effects.isEmpty())
            }
        }
    }

    @Test
    fun pagesRestoreVisibleContentAndKeepHiddenWritesAndCursorOffscreen() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(12, 4), 1).use { terminal ->
            terminal.feed(1, "ONE\u001b[2 P\u001b[HTWO\u001b[?64l\u001b[1 P\u001b[HHIDDEN".encodeToByteArray())
            assertEquals("T", firstCellText(terminal.renderV4().renderFrame))
            assertFalse(terminal.renderV4().renderFrame.cursor.visible)
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                val replies = copy.feedWithHostEffectsV2(2, "\u001b[\"v\u001b[?6n\u001b[1\$w".encodeToByteArray()).effects
                assertEquals(3, replies.size)
                assertArrayEquals("\u001b[4;12;1;1;2\"w".encodeToByteArray(), replies[0].copyPayload())
                assertArrayEquals("\u001b[?1;7;1R".encodeToByteArray(), replies[1].copyPayload())
                copy.feed(3, "\u001b[?64h".encodeToByteArray())
                assertEquals("H", firstCellText(copy.renderV4().renderFrame))
                assertTrue(copy.renderV4().renderFrame.cursor.visible)
            }
        }
    }

    @Test
    fun mixedMacroDiagnosticLabelsPhysicalAndExpandedBytesAcrossJni() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(12, 4), 1).use { terminal ->
            terminal.feed(1, "\u001bP5;0;1!z1B5B39\u001b\\\u001b[5*z".encodeToByteArray())
            try { terminal.feed(2, "99m".encodeToByteArray()); throw AssertionError("Mixed unknown SGR accepted") }
            catch (error: NativeTerminalException) {
                val diagnostic = error.terminalProtocolDiagnostic?.text
                assertTrue(diagnostic?.contains("expanded_source=mixed\n") == true)
                assertTrue(diagnostic?.contains("expanded_origins=macro,5,0,3;output,2,0,3\n") == true)
                assertTrue(diagnostic?.contains("expanded_sequence=<ESC>[999m\n") == true)
            }
        }
    }

    @Test
    fun macrosRestoreRenderAndRejectRecursionWithoutAdvancingTheSource() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(20, 6), 1).use { terminal ->
            terminal.feed(1, "\u001bP1;0;1!z1B5B356E1B5B356E4F4B\u001b\\".encodeToByteArray())
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                val replies = copy.feedWithHostEffectsV2(2, "\u001b[1*z\u001b[?62n\u001b[?63;42n".encodeToByteArray()).effects
                assertEquals(4, replies.size)
                assertArrayEquals("\u001b[16383*{".encodeToByteArray(), replies[2].copyPayload())
                assertEquals("O", firstCellText(copy.renderV4().renderFrame))
                copy.feed(3, "\u001bP0;0;1!z1B5B302A7A\u001b\\".encodeToByteArray())
                try { copy.feedWithHostEffectsV2(4, "\u001b[0*z".encodeToByteArray()); throw AssertionError("Recursive macro accepted") }
                catch (error: NativeTerminalException) {
                    assertTrue(error.terminalProtocolDiagnostic?.text?.contains("DECINVM nesting exceeds 16 invocations") == true)
                }
                assertEquals(4L, copy.nextSourceSequence)
                copy.feed(4, "LIVE".encodeToByteArray())
                copy.feed(5, "\u001bP5;0;1!z1B5B3939396D\u001b\\".encodeToByteArray())
                try { copy.feed(6, "\u001b[5*z".encodeToByteArray()); throw AssertionError("Unknown macro SGR accepted") }
                catch (error: NativeTerminalException) {
                    val text = error.terminalProtocolDiagnostic?.text
                    assertTrue(text?.startsWith("TL-DIAG/3\n") == true)
                    assertTrue(text?.contains("expanded_sequence=<ESC>[999m\n") == true)
                    assertTrue(text?.contains("expanded_start=5:0\nexpanded_end=5:6\n") == true)
                }
            }
        }
    }

    @Test
    fun traditionalModesPreserveInputRepliesAndGlyphsAcrossJniSnapshots() {
        val prefix = "\u001b%@\u001bP1!uB\u001b\\\u001b-<\u000e!\u001b G".encodeToByteArray()
        for (split in 0..prefix.size) {
            NativeTerminalHandle.create(JniNativeBridge, TerminalSize(20, 6), 1).use { terminal ->
                if (split == 0) terminal.advance(1, 0) else terminal.feed(1, prefix.copyOfRange(0, split))
                if (split == prefix.size) terminal.advance(2, 0) else terminal.feed(2, prefix.copyOfRange(split, prefix.size))
                assertEquals("Ą", firstCellText(terminal.renderV4().renderFrame))
                NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                    assertArrayEquals(byteArrayOf(0x9b.toByte(), 0x41), copy.encodeKey(TerminalKey.ARROW_UP))
                    val replies = copy.feedWithHostEffectsV2(3, "\u001b[&u\u001b[5n".encodeToByteArray()).effects
                    assertEquals(2, replies.size)
                    assertArrayEquals(byteArrayOf(0x90.toByte(), 0x31, 0x21, 0x75, 0x42, 0x9c.toByte()), replies[0].copyPayload())
                    assertArrayEquals(byteArrayOf(0x9b.toByte(), 0x30, 0x6e), replies[1].copyPayload())
                    val vt52 = copy.feedWithHostEffectsV2(4, "\u000f\u001b%G\u001b F\u001b[?2l\u001bHV\u001bZ".encodeToByteArray())
                    assertArrayEquals("\u001b/Z".encodeToByteArray(), vt52.effects.single().copyPayload())
                    NativeTerminalHandle.importCheckpoint(JniNativeBridge, copy.exportCheckpoint().copyPayload()).use { legacy ->
                        assertArrayEquals("\u001bA".encodeToByteArray(), legacy.encodeKey(TerminalKey.ARROW_UP))
                        legacy.feed(5, "\u001b<".encodeToByteArray())
                        assertArrayEquals("\u001b[A".encodeToByteArray(), legacy.encodeKey(TerminalKey.ARROW_UP))
                    }
                }
            }
        }
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(20, 6), 1).use { invalid ->
            try { invalid.feed(1, "\u001bP0002!uB\u001b\\".encodeToByteArray()); throw AssertionError("Invalid UPSS accepted") }
            catch (error: NativeTerminalException) {
                assertTrue(error.terminalProtocolDiagnostic?.text?.contains("rejected_parameter=0002\n") == true)
            }
        }
    }

    @Test
    fun assignedPaletteAndSelectionAreRenderedAndRestored() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val engine = NativeTerminalEngine(TerminalSize(20, 5))
        val output = "\u001b[?25l\u001b]17;#123456\u001b\\\u001b]4;2;#abcdef;3;#2468ac\u001b\\\u001b[1;2;3,|\u001b[2;2;3,|"
        try {
            engine.feed(1, ByteBuffer.wrap(output.encodeToByteArray()))
            val frame = engine.frames.value
            assertEquals(0x123456, frame.palette.selectionBackgroundRgb)
            assertEquals(2, frame.palette.foregroundAlias)
            assertEquals(3, frame.palette.backgroundAlias)
            instrumentation.runOnMainSync {
                val view = NativeTerminalView(instrumentation.targetContext, engine, 14f, 1f)
                view.layout(0, 0, 900, 420); view.seedSelectionForTest()
                val bitmap = android.graphics.Bitmap.createBitmap(900, 420, android.graphics.Bitmap.Config.ARGB_8888)
                view.drawFrame(Canvas(bitmap), frame, engine.theme())
                val padding = (8 * instrumentation.targetContext.resources.displayMetrics.density).toInt()
                assertEquals(0xff123456.toInt(), bitmap.getPixel(padding + 2, padding + 2))
                assertEquals(0xff2468ac.toInt(), bitmap.getPixel(450, 400))
                val file = java.io.File(instrumentation.targetContext.getExternalFilesDir(null), "assigned-colors-v4.png")
                file.outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
                bitmap.recycle()
            }
            NativeTerminalHandle.create(JniNativeBridge, TerminalSize(20, 5), 1).use { terminal ->
                terminal.feed(1, output.encodeToByteArray())
                NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                    assertEquals(terminal.renderV4().renderFrame.palette.backgroundAlias, copy.renderV4().renderFrame.palette.backgroundAlias)
                    val replies = copy.feedWithHostEffectsV2(2, "\u001b]17;?\u001b\\\u001bP\$q1,|\u001b\\".encodeToByteArray())
                    assertEquals(2, replies.effects.size)
                }
            }
        } finally { engine.dispose() }
    }

    @Test
    fun scaledLinesRenderAndRestoreThroughJni() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val engine = NativeTerminalEngine(TerminalSize(20, 5))
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[?25lWWWW\u001b[2;1H\u001b#6WWWW\u001b[3;1H\u001b#3ABCD\u001b[4;1H\u001b#4ABCD".encodeToByteArray()))
            val frame = engine.frames.value
            assertEquals(dev.termlink.terminal.api.TerminalLineRendition.DOUBLE_WIDTH, frame.rows[1].lineRendition)
            assertEquals(dev.termlink.terminal.api.TerminalLineRendition.DOUBLE_HEIGHT_TOP, frame.rows[2].lineRendition)
            assertEquals(dev.termlink.terminal.api.TerminalLineRendition.DOUBLE_HEIGHT_BOTTOM, frame.rows[3].lineRendition)
            instrumentation.runOnMainSync {
                val view = NativeTerminalView(instrumentation.targetContext, null, 14f, 1f)
                view.layout(0, 0, 900, 420)
                val bitmap = android.graphics.Bitmap.createBitmap(900, 420, android.graphics.Bitmap.Config.ARGB_8888)
                view.drawFrame(Canvas(bitmap), frame, engine.theme())
                val cellHeight = view.transcriptMetricsForTest().split("/")[1].toFloat()
                val padding = 8f * instrumentation.targetContext.resources.displayMetrics.density
                val background = bitmap.getPixel(0, 0)
                fun inkWidth(row: Int): Int {
                    var left = bitmap.width; var right = -1
                    for (y in (padding + row * cellHeight).toInt() until (padding + (row + 1) * cellHeight).toInt()) {
                        for (x in 0 until bitmap.width) if (kotlin.math.abs(android.graphics.Color.red(bitmap.getPixel(x, y)) - android.graphics.Color.red(background)) > 16) { left = minOf(left, x); right = maxOf(right, x) }
                    }
                    return right - left + 1
                }
                val file = java.io.File(instrumentation.targetContext.getExternalFilesDir(null), "scaled-lines-v4.png")
                file.outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
                assertTrue("scaled text must occupy twice the ink width: normal=${inkWidth(0)}, scaled=${inkWidth(1)}, height=$cellHeight, padding=$padding", inkWidth(1) in (inkWidth(0) * 2 - 4)..(inkWidth(0) * 2 + 4))
                assertTrue(inkWidth(2) > 0 && inkWidth(3) > 0)
                bitmap.recycle()
            }
            NativeTerminalHandle.create(JniNativeBridge, TerminalSize(9, 4), 1).use { terminal ->
                terminal.feed(1, "\u001b#6ABCD\u001b[2;1H\u001b#3TOP".encodeToByteArray())
                NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { copy ->
                    assertEquals(terminal.renderV4().renderFrame.rows[0].lineRendition, copy.renderV4().renderFrame.rows[0].lineRendition)
                    assertEquals(terminal.renderV4().renderFrame.rows[1].lineRendition, copy.renderV4().renderFrame.rows[1].lineRendition)
                }
            }
        } finally { engine.dispose() }
    }

    @Test
    fun diagnosticIdentifiesExactColorComponentThroughJni() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(16, 4), 1).use { terminal ->
            try {
                terminal.feed(1, "\u001b[1;58:2::255:00256:1m".encodeToByteArray())
                throw AssertionError("invalid RGB component was accepted")
            } catch (error: NativeTerminalException) {
                assertTrue(error.terminalProtocolDiagnostic?.text?.contains("rejected_parameter_index=5\nrejected_parameter=00256\n") == true)
            }
        }
    }
    @Test
    fun unicode16EngineDrawsWholeClustersAndKeepsCopyText() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val family = "👨\u200d👩\u200d👧\u200d👦"
        val text = "🇨🇳 👍🏽 $family a\u0301 क्\u200dष"
        val engine = NativeTerminalEngine(TerminalSize(32, 4),
            widthProfile = dev.termlink.terminal.api.TerminalWidthProfile.UNICODE_16)
        try {
            engine.feed(1, ByteBuffer.wrap(text.encodeToByteArray()))
            val frame = engine.frames.value
            assertEquals(4, frame.formatVersion)
            assertTrue(frameText(frame).startsWith(text))
            val recording = TextRecordingCanvas()
            instrumentation.runOnMainSync {
                val view = NativeTerminalView(instrumentation.targetContext, null, 14f, 1f)
                view.layout(0, 0, 720, 180)
                view.drawFrame(recording, frame, engine.theme())
                val bitmap = android.graphics.Bitmap.createBitmap(720, 180, android.graphics.Bitmap.Config.ARGB_8888)
                view.drawFrame(Canvas(bitmap), frame, engine.theme())
                val file = java.io.File(instrumentation.targetContext.getExternalFilesDir(null), "unicode16-render-v4.png")
                file.outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
                bitmap.recycle()
            }
            assertTrue(recording.texts.contains(family))
            assertTrue(recording.texts.contains("🇨🇳"))
            assertTrue(recording.texts.contains("क्\u200dष"))
        } finally { engine.dispose() }
    }
    @Test
    fun unicode16V4SurvivesJniScrollbackAndCheckpointRestore() {
        val family = "👨\u200d👩\u200d👧\u200d👦"
        NativeTerminalHandle.createWithProfiles(JniNativeBridge, TerminalSize(16, 4), 1,
            TerminalOutputProfile.UTF8, dev.termlink.terminal.api.TerminalWidthProfile.UNICODE_16,
            historyCapacityRows = 20).use { terminal ->
            val text = "🇨🇳$family\r\n2\r\n3\r\n4\r\n5"
            var sequence = 1L
            for (byte in text.encodeToByteArray()) terminal.feed(sequence++, byteArrayOf(byte))
            val history = terminal.renderV4AtScrollback(99)
            assertTrue(history.renderFrame.graphemes.any { it.text == family })
            assertEquals(4, history.renderFrame.formatVersion)
            assertEquals(dev.termlink.terminal.api.TerminalWidthProfile.UNICODE_16, history.renderFrame.widthProfile)
            try {
                terminal.renderV3()
                throw AssertionError("legacy render accepted modern width profile")
            } catch (expected: NativeTerminalException) {
                assertEquals(NativeTerminalStatus.RENDER_FRAME_VERSION_REQUIRED, expected.status)
            }
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { restored ->
                terminal.feed(sequence, "$family!".encodeToByteArray())
                restored.feed(sequence, "$family!".encodeToByteArray())
                assertEquals(terminal.renderV4().renderFrame.cursor, restored.renderV4().renderFrame.cursor)
                assertArrayEquals(terminal.exportCheckpoint().copyPayload(), restored.exportCheckpoint().copyPayload())
            }
        }
    }
    @Test
    fun decStateReportsRestoreThroughJniWithoutRepeatingOnCheckpointImport() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(17, 4), 1).use { terminal ->
            val reports = terminal.feedWithHostEffectsV2(1, "\u001b[1\$w\u001b[2\$w".encodeToByteArray()).effects.map { it.copyPayload().decodeToString() }
            assertEquals(listOf("\u001bP1\$u1;1;1;@;@;@;0;2;L;BBAA\u001b\\", "\u001bP2\$u9/17\u001b\\"), reports)
            val restore = "\u001bP2\$t4/132/512\u001b\\\u001bP2\$p0;2;100;0;0\u001b\\".encodeToByteArray()
            var sequence = 2L
            for (byte in restore) assertTrue(terminal.feedWithHostEffectsV2(sequence++, byteArrayOf(byte)).effects.isEmpty())
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, terminal.exportCheckpoint().copyPayload()).use { reopened ->
                val reply = reopened.feedWithHostEffectsV2(sequence, "\u001b[2\$w\u001b[2;2\$u\r\tX".encodeToByteArray())
                assertEquals(listOf("\u001bP2\$u4\u001b\\", "\u001bP2\$s0;2;100;0;0\u001b\\"), reply.effects.map { it.copyPayload().decodeToString() })
                assertEquals(4, reopened.renderV3().renderFrame.cursor.column)
            }
        }
    }

    @Test
    fun alternateScrollEncodesArrowsWithoutEnablingTouchMouseMode() = runBlocking {
        val engine = NativeTerminalEngine(TerminalSize(80, 24))
        val wheel = TerminalMouseInput.Wheel(
            direction = dev.termlink.terminal.api.TerminalMouseWheelDirection.UP,
            position = TerminalMousePosition(TerminalCellPosition(0, 0)),
        )
        try {
            engine.setReadOnly(false)
            engine.feed(1, ByteBuffer.wrap("\u001b[?1007h".encodeToByteArray()))
            assertFalse(engine.submitMouse(wheel))
            engine.feed(2, ByteBuffer.wrap("\u001b[?1049h\u001b[?1h".encodeToByteArray()))
            assertFalse("alternate-scroll is not touch mouse reporting", engine.mouseReporting.value)
            val pending = async(start = CoroutineStart.UNDISPATCHED) { engine.inputEvents.first() }
            assertTrue(engine.submitMouse(wheel))
            assertArrayEquals("\u001bOA".encodeToByteArray(), withTimeout(1000) { pending.await() })
            engine.feed(3, ByteBuffer.wrap("\u001b[?1007l".encodeToByteArray()))
            assertFalse(engine.submitMouse(wheel))
        } finally { engine.dispose() }
    }

    @Test
    fun horizontalIndexAndDefaultTabsSurviveJniAndCheckpoint() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(16, 2), 1).use { terminal ->
            terminal.feedWithHostEffectsV2(1, "ABCDEFGHIJKLMNOQ\u001b[2;1Habcdefghijklmnoq\u001b[1;1H\u001b6\u001b[2;16H\u001b9\u001b[3g\u001b[?5W\r\t".encodeToByteArray())
            val checkpoint = terminal.exportCheckpoint().copyPayload()
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint).use { restored ->
                val frame = restored.renderV3().renderFrame
                fun row(index: Int) = frame.rows[index].cells.joinToString("") { cell ->
                    if (cell.wideContinuation) "" else frame.graphemes[cell.graphemeId - 1].text
                }
                assertEquals("ABCDEFGHIJKLMNO ", row(0))
                assertEquals("abcdefghijklmno ", row(1))
                assertEquals(8, frame.cursor.column)
            }
        }
    }

    @Test
    fun claudeGeometryQueriesReplayWithoutRetiringNativeTerminal() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(120, 30), 1).use { terminal ->
            terminal.feedWithHostEffectsV2(1, "SHELL".encodeToByteArray())
            val snapshot = terminal.exportCheckpoint().copyPayload()
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, snapshot).use { restored ->
                val suffix = "\u001b[?2026\$p\u001b[16t\u001b[?1016\$p\u001b[c\u001b[18t".encodeToByteArray()
                val replies = mutableListOf<String>()
                var sequence = 2L
                for (byte in suffix) {
                    replies += restored.feedWithHostEffectsV2(sequence++, byteArrayOf(byte))
                        .effects.map { it.copyPayload().decodeToString() }
                }
                assertEquals(listOf("\u001b[?2026;2\$y", "\u001b[?1016;2\$y", "\u001b[?1;2c", "\u001b[8;30;120t"), replies)
                restored.feedWithHostEffectsV2(sequence++, "\u001b[14;2t\u001b[15t\u001b[19t\u001b[!p_ALIVE".encodeToByteArray())
                assertEquals("SHELL_ALIVE", frameText(restored.renderV3().renderFrame).trim())
                val after = restored.exportCheckpoint().copyPayload()
                NativeTerminalHandle.importCheckpoint(JniNativeBridge, after).use { reopened ->
                    reopened.feedWithHostEffectsV2(sequence, "!".encodeToByteArray())
                    assertEquals("SHELL_ALIVE!", frameText(reopened.renderV3().renderFrame).trim())
                }
            }
        }
    }

    @Test
    fun auxiliaryWheelUsesViewportTargetAndCoreModesWithoutClicks() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val activity = instrumentation.startActivitySync(android.content.Intent(
            instrumentation.context, ViewportTestActivity::class.java,
        ).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
        val engine = NativeTerminalEngine(TerminalSize(80, 24))
        val up = dev.termlink.terminal.api.TerminalMouseWheelDirection.UP
        val down = dev.termlink.terminal.api.TerminalMouseWheelDirection.DOWN
        suspend fun next() = withTimeout(1000) { engine.inputEvents.first() }.decodeToString()
        try {
            engine.feed(1, ByteBuffer.wrap("\u001b[?1003;1016h".encodeToByteArray()))
            engine.setReadOnly(false)
            instrumentation.runOnMainSync {
                val view = engine.createView(activity) as NativeTerminalView
                activity.setContentView(view)
                view.layout(0, 0, 500, 500)
                assertTrue(engine.submitMouseWheel(up))
                val touch = MotionEvent.obtain(0, 0, MotionEvent.ACTION_DOWN, 100f, 150f, 0)
                val cancel = MotionEvent.obtain(0, 1, MotionEvent.ACTION_CANCEL, 100f, 150f, 0)
                try { view.dispatchTouchEvent(touch); view.dispatchTouchEvent(cancel) }
                finally { touch.recycle(); cancel.recycle() }
                assertTrue(engine.submitMouseWheel(down))
                engine.setReadOnly(true)
                assertFalse(engine.submitMouseWheel(up))
                engine.setReadOnly(false)
                engine.setTouchMouseEnabled(true)
                assertFalse(engine.submitMouseWheel(up))
                engine.setTouchMouseEnabled(false)
            }
            assertEquals("\u001b[<64;251;251M", next())
            assertEquals("\u001b[<65;101;151M", next())
            engine.feed(2, ByteBuffer.wrap("\u001b[?1016l\u001b[?1006h".encodeToByteArray()))
            instrumentation.runOnMainSync { assertTrue(engine.submitMouseWheel(up)) }
            assertTrue(next().matches(Regex("\u001b\\[<64;[0-9]+;[0-9]+M")))
            engine.feed(3, ByteBuffer.wrap("\u001b[?1003l".encodeToByteArray()))
            instrumentation.runOnMainSync { assertFalse(engine.submitMouseWheel(up)) }
            assertEquals(null, withTimeoutOrNull(80) { engine.inputEvents.first() })
        } finally { instrumentation.runOnMainSync { engine.dispose(); activity.finish() } }
    }

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
    fun renditionStackSurvivesRealJniCheckpointAndSelectiveRestore() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(8, 2), 1).use { terminal ->
            terminal.feed(1, "\u001b[2;4:3;31m\u001b[#{\u001b[0m".encodeToByteArray())
            val checkpoint = terminal.exportCheckpoint().copyPayload()
            assertEquals(11, checkpoint[10].toInt())
            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint).use { restored ->
                terminal.feed(2, "\u001b[#qX".encodeToByteArray())
                restored.feed(2, "\u001b[#qX".encodeToByteArray())
                assertArrayEquals(terminal.exportCheckpoint().copyPayload(), restored.exportCheckpoint().copyPayload())
                val frame = restored.renderV3().renderFrame
                assertEquals("X", firstCellText(frame))
                assertTrue(frame.renditions[frame.rows[0].cells[0].renditionId].faint)
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
        val effects = arrayOfNulls<ByteArray>(1)
        val diagnostic = arrayOfNulls<ByteArray>(1)
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code,
            JniNativeMethods.applyBrokerUnsupportedV1(token, 1, byteArrayOf(65), effects, diagnostic),
        )
        assertEquals(null, effects[0])
        assertEquals(null, diagnostic[0])
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
    fun unsupportedOutputExposesExactRedactedDiagnosticThroughJni() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(80, 24), initialNextSourceSequence = 1).use { terminal ->
            terminal.feedWithHostEffectsV2(1, "ok\u001b[?".encodeToByteArray())
            val error = runCatching {
                terminal.feedWithHostEffectsV2(2, "9999h".encodeToByteArray())
            }.exceptionOrNull() as NativeTerminalException
            assertEquals(NativeTerminalStatus.UNSUPPORTED, error.status)
            val diagnostic = error.terminalProtocolDiagnostic!!
            assertEquals("<ESC>[?9999h", diagnostic.sequence)
            assertTrue(diagnostic.text.startsWith("TL-DIAG/2\n"))
            assertTrue(diagnostic.text.contains("detected_at=2:4\nlast_byte=68\nlast_codepoint=U+0068\n"))
            assertTrue(diagnostic.text.contains("core_impact=failed_closed\n"))
            assertTrue(diagnostic.text.contains("start=1:2\nend=2:5\n"))
            assertTrue(diagnostic.text.contains("hex=1B 5B 3F 39 39 39 39 68\n"))
            val repeated = runCatching { terminal.feed(2, "retry".encodeToByteArray()) }.exceptionOrNull()
                as NativeTerminalException
            assertEquals(diagnostic.text, repeated.terminalProtocolDiagnostic!!.text)
        }
    }

    @Test
    fun unsupportedInputExposesSameCallCharacterAndKeyWithoutOutputCoordinates() {
        NativeTerminalHandle.create(JniNativeBridge, TerminalSize(80, 24), 1).use { terminal ->
            val rejected = runCatching {
                terminal.encodeCharacter('é'.code, TerminalInputModifiers(control = true))
            }.exceptionOrNull() as NativeTerminalException
            assertTrue("JNI input diagnostic call failed: ${rejected.cause}", rejected.cause == null)
            assertEquals(NativeTerminalStatus.UNSUPPORTED, rejected.status)
            val diagnostic = requireNotNull(rejected.terminalInputDiagnostic)
            assertEquals("kitty_key", diagnostic.kind)
            assertEquals("unsupported_control_character", diagnostic.reason)
            assertEquals("Character", diagnostic.key)
            assertEquals("U+00E9", diagnostic.codepoint)
            assertEquals("é", diagnostic.characterPreview)
            assertEquals("0x04", diagnostic.modifiers)
            assertTrue(diagnostic.text.startsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n"))
            assertFalse(diagnostic.text.contains("start="))
            assertEquals(null, rejected.terminalProtocolDiagnostic)
            assertArrayEquals(byteArrayOf(3),
                terminal.encodeCharacter('c'.code, TerminalInputModifiers(control = true)))

            val win32 = runCatching { terminal.encodeWin32Key(0x41, 0x1e, 'a'.code, true, 0, 1) }
                .exceptionOrNull() as NativeTerminalException
            assertEquals("win32_input_mode_disabled", win32.terminalInputDiagnostic?.reason)
            assertEquals("VK_0041", win32.terminalInputDiagnostic?.key)
            assertEquals("none", win32.terminalInputDiagnostic?.codepoint)
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
    fun extendedReportsCrossTheRealJniBoundaryExactlyOnce() {
        NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(80, 24),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8,
            historyCapacityRows = 10_000,
        ).use { terminal ->
            val frame = terminal.feedWithHostEffectsV2(
                1,
                "\u001b[=c\u001b[x\u001b[?15n\u001b[\"v\u001b[42;1;1;1;1;1*y".encodeToByteArray(),
            )
            val expected = listOf(
                "\u001bP!|00000000\u001b\\", "\u001b[2;1;1;128;128;1;0x",
                "\u001b[?13n", "\u001b[24;80;1;1;1\"w", "\u001bP42!~FF70\u001b\\",
            )
            assertEquals(expected.size, frame.effects.size)
            frame.effects.zip(expected).forEach { (effect, bytes) ->
                assertArrayEquals(bytes.encodeToByteArray(), effect.copyPayload())
            }
            assertTrue(terminal.feedWithHostEffectsV2(2, "\u001b[999nALIVE".encodeToByteArray()).effects.isEmpty())
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

    private fun firstCellText(frame: dev.termlink.terminal.api.TerminalTextFrame): String {
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

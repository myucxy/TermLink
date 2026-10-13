package dev.termlink.terminal.nativecore

import android.view.KeyEvent
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalWidthProfile
import dev.termlink.terminal.api.TerminalKittyKeyEventType
import java.nio.ByteBuffer
import java.util.zip.CRC32C
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.assertSame
import org.junit.Assert.assertNull
import kotlin.test.assertFailsWith
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeTerminalEngineTest {
    @Test
    fun rejectedInputShowsOnlyTypedDiagnosticUntilSuccessOrDispose() {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(TerminalSize(80, 24), bridge)
        val diagnostic = "TL-INPUT-DIAG/1\ndirection=input_encoding\nstatus=unsupported\n" +
            "kind=kitty_key\nreason=unsupported_control_character\nkey=Character\n" +
            "codepoint=U+00E9\nmodifiers=0x04\n"
        val exception = NativeTerminalException("encodeKittyKey", NativeTerminalStatus.UNSUPPORTED,
            diagnostic = diagnostic)
        assertEquals(diagnostic, exception.terminalInputDiagnostic?.text)
        assertNull(exception.terminalProtocolDiagnostic)
        assertFalse(exception.message.orEmpty().contains(diagnostic))
        try {
            engine.setReadOnly(false)
            bridge.forcedInputV3Failure = NativeBridgeResult.Failure(
                NativeTerminalStatus.UNSUPPORTED.code, diagnostic)
            assertFalse(engine.submitCodePoint('é'.code, controlDown = true, altDown = false,
                shiftDown = false, shiftedCodePoint = null))
            val first = requireNotNull(engine.inputDiagnostic.value)
            assertEquals("U+00E9", first.codepoint)
            assertEquals("unsupported_control_character", first.reason)
            assertNull(first.let { dev.termlink.terminal.api.TerminalProtocolDiagnostic.parse(it.text) })

            assertFalse(engine.submitCodePoint('é'.code, controlDown = true, altDown = false,
                shiftDown = false, shiftedCodePoint = null))
            val second = requireNotNull(engine.inputDiagnostic.value)
            engine.dismissInputDiagnostic(first)
            assertSame(second, engine.inputDiagnostic.value)
            bridge.forcedInputV3Failure = null
            assertTrue(engine.submitCodePoint('a'.code, controlDown = false, altDown = false,
                shiftDown = false, shiftedCodePoint = null))
            assertNull(engine.inputDiagnostic.value)

            bridge.forcedInputV3Failure = NativeBridgeResult.Failure(
                NativeTerminalStatus.UNSUPPORTED.code, diagnostic)
            assertFalse(engine.submitCodePoint('é'.code, controlDown = true, altDown = false,
                shiftDown = false, shiftedCodePoint = null))
            assertEquals("U+00E9", engine.inputDiagnostic.value?.codepoint)
        } finally { engine.dispose() }
        assertNull(engine.inputDiagnostic.value)
    }

    @Test
    fun checkpointProfilesCannotChangeTheConfiguredEngineOrInputPermission() {
        for ((width, output) in listOf(TerminalWidthProfile.UNICODE_16 to TerminalOutputProfile.UTF8,
            TerminalWidthProfile.LEGACY_V1 to TerminalOutputProfile.UTF8_WITH_STANDALONE_C1)) {
            val delegate = GraphicsTestBridge()
            val bridge = graphicsProfileBridge(delegate, importedWidth = width)
            val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge, graphicsProfile = true)
            try {
                engine.setReadOnly(false)
                val original = engine.frames.value
                val error = assertFailsWith<NativeTerminalException> {
                    engine.installCheckpoint(profileSnapshot(22, output))
                }
                assertEquals(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT, error.status)
                assertSame(original, engine.frames.value)
                assertEquals(1L, delegate.fake.onlyStateForTest().nextSourceSequence)
                assertTrue(engine.canAcceptInput())
                assertEquals(1, delegate.fake.destroyCalls)
            } finally { engine.dispose() }
            assertEquals(2, delegate.fake.destroyCalls)
        }
    }

    @Test
    fun explicitlyConfiguredUnicodeAndC1EngineAcceptsItsMatchingCheckpoint() {
        val delegate = GraphicsTestBridge()
        val bridge = graphicsProfileBridge(delegate, initialWidth = TerminalWidthProfile.UNICODE_16,
            importedWidth = TerminalWidthProfile.UNICODE_16)
        val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge,
            outputProfile = TerminalOutputProfile.UTF8_WITH_STANDALONE_C1,
            widthProfile = TerminalWidthProfile.UNICODE_16, graphicsProfile = true)
        try {
            assertEquals(1L, engine.installCheckpoint(profileSnapshot(22, TerminalOutputProfile.UTF8_WITH_STANDALONE_C1)).sequence)
            assertEquals(TerminalWidthProfile.UNICODE_16, engine.frames.value.widthProfile)
            assertEquals(1, delegate.fake.destroyCalls)
        } finally { engine.dispose() }
    }

    @Test
    fun legacyCheckpointProfileValidationKeepsKnownVersionsAndRejectsUnknownStubBytes() {
        for ((minor, output) in listOf(0 to TerminalOutputProfile.UTF8,
            8 to TerminalOutputProfile.UTF8_WITH_STANDALONE_C1, 21 to TerminalOutputProfile.UTF8)) {
            val fake = FakeNativeBridge()
            val bridge = acceptingLegacyImport(fake)
            val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge, outputProfile = output)
            try {
                assertEquals(1L, engine.installCheckpoint(profileSnapshot(minor, output)).sequence)
                assertEquals(1, fake.destroyCalls)
            } finally { engine.dispose() }
        }
        for (checkpoint in listOf(byteArrayOf(1), profileSnapshot(23, TerminalOutputProfile.UTF8),
            profileSnapshot(21, TerminalOutputProfile.UTF8).also { graphicsPut16(it, 14, 96) })) {
            val fake = FakeNativeBridge()
            val engine = NativeTerminalEngine(TerminalSize(2, 1), acceptingLegacyImport(fake))
            try {
                val original = engine.frames.value
                assertFailsWith<NativeTerminalContractException> { engine.installCheckpoint(checkpoint) }
                assertSame(original, engine.frames.value)
                assertEquals(1, fake.destroyCalls)
            } finally { engine.dispose() }
        }
    }

    @Test
    fun checkpointProfileUsesTheBytesImportedByNativeWhenCallerMutatesItsArray() {
        val callerCheckpoint = profileSnapshot(21, TerminalOutputProfile.UTF8_WITH_STANDALONE_C1)
        val fake = FakeNativeBridge()
        val bridge = object : NativeBridge by fake {
            override fun importCheckpoint(checkpoint: ByteArray): NativeBridgeResult<NativeBridgeTerminal> {
                assertEquals(0x10, checkpoint[66].toInt())
                graphicsPut64(callerCheckpoint, 64, 0)
                return fake.create(TerminalSize(2, 1), 2)
            }
        }
        val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge)
        try {
            engine.setReadOnly(false)
            val original = engine.frames.value
            assertEquals(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT,
                assertFailsWith<NativeTerminalException> { engine.installCheckpoint(callerCheckpoint) }.status)
            assertSame(original, engine.frames.value)
            assertTrue(engine.canAcceptInput())
            assertEquals(1, fake.destroyCalls)
        } finally { engine.dispose() }
    }

    private fun acceptingLegacyImport(fake: FakeNativeBridge): NativeBridge = object : NativeBridge by fake {
        override fun importCheckpoint(checkpoint: ByteArray): NativeBridgeResult<NativeBridgeTerminal> =
            fake.create(TerminalSize(2, 1), 2)
    }

    private fun profileSnapshot(minor: Int, output: TerminalOutputProfile): ByteArray = graphicsSnapshot().also {
        graphicsPut16(it, 10, minor); graphicsPut16(it, 12, minor)
        graphicsPut64(it, 64, if (output == TerminalOutputProfile.UTF8_WITH_STANDALONE_C1) 0x10_0000L else 0L)
    }

    private fun graphicsProfileBridge(delegate: GraphicsTestBridge,
        initialWidth: TerminalWidthProfile = TerminalWidthProfile.LEGACY_V1,
        importedWidth: TerminalWidthProfile): NativeBridge = object : NativeBridge by delegate {
        override fun renderV5AtScrollback(handle: NativeBridgeHandle, offsetRows: Long,
            request: NativeRenderRequest): NativeBridgeResult<NativeBridgeScrollbackFrame> {
            return when (val result = delegate.renderV5AtScrollback(handle, offsetRows, request)) {
                is NativeBridgeResult.Failure -> result
                is NativeBridgeResult.Success -> {
                    val raw = result.value
                    val bytes = raw.payload
                    val width = if (ByteBuffer.wrap(bytes).order(java.nio.ByteOrder.LITTLE_ENDIAN).getLong(16) == 1L)
                        initialWidth else importedWidth
                    graphicsPut32(bytes, 64 + 60, width.wireValue)
                    graphicsPut32(bytes, 64 + 92, CRC32C().apply { update(bytes, 64, 92) }.value.toInt())
                    graphicsSeal(bytes, 52)
                    NativeBridgeResult.Success(raw)
                }
            }
        }
    }

    @Test
    fun keyboardInputsUseInputV3WithoutPasteFallback() {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            engine.setReadOnly(false)

            assertTrue(engine.submitText("/"))
            assertEquals(NATIVE_INPUT_KITTY_KEY_UNIDENTIFIED_TEXT, bridge.lastInputV3?.key)
            assertEquals("/", bridge.lastInputV3?.copyAssociatedText()?.decodeToString())

            assertTrue(engine.submitCodePoint('c'.code, controlDown = true))
            assertEquals(NATIVE_INPUT_KEY_CHARACTER, bridge.lastInputV3?.key)
            assertEquals('c'.code, bridge.lastInputV3?.character)
            assertEquals(NATIVE_KITTY_MODIFIER_CONTROL, bridge.lastInputV3?.kittyModifiers)

            assertTrue(
                engine.submitCodePoint(
                    '/'.code,
                    shiftDown = true,
                    shiftedCodePoint = '?'.code,
                ),
            )
            assertEquals('/'.code, bridge.lastInputV3?.character)
            assertEquals('?'.code, bridge.lastInputV3?.kittyShiftedKey)
            assertEquals(NATIVE_KITTY_MODIFIER_SHIFT, bridge.lastInputV3?.kittyModifiers)

            assertTrue(engine.submitKeyCode(KeyEvent.KEYCODE_DPAD_UP))
            assertEquals(12L, bridge.lastInputV3?.key)
            assertEquals(4, bridge.inputV3Calls)
            assertEquals(0, bridge.inputV1Calls)

            assertTrue(engine.submitPaste("paste"))
            assertEquals(NATIVE_INPUT_KIND_PASTE, bridge.lastInputV1?.kind)
            assertEquals(1, bridge.inputV1Calls)
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun physicalKeyboardPreservesEventPhaseKeypadLocksAndRejectsUnsupportedInputSafely() {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(TerminalSize(80, 24), bridge)
        try {
            engine.setReadOnly(false)

            assertTrue(engine.submitPhysicalKeyCode(
                KeyEvent.KEYCODE_NUMPAD_ENTER,
                controlDown = false,
                altDown = false,
                shiftDown = false,
                capsLock = true,
                numLock = true,
                eventType = TerminalKittyKeyEventType.REPEAT,
            ))
            assertEquals(117L, bridge.lastInputV3?.key)
            assertEquals(NATIVE_KITTY_EVENT_REPEAT, bridge.lastInputV3?.kittyEventType)
            assertEquals(
                NATIVE_KITTY_MODIFIER_CAPS_LOCK or NATIVE_KITTY_MODIFIER_NUM_LOCK,
                bridge.lastInputV3?.kittyModifiers,
            )

            assertTrue(engine.submitPhysicalKeyCode(
                KeyEvent.KEYCODE_NUMPAD_ENTER,
                controlDown = false,
                altDown = false,
                shiftDown = false,
                eventType = TerminalKittyKeyEventType.RELEASE,
            ))
            assertEquals(NATIVE_KITTY_EVENT_RELEASE, bridge.lastInputV3?.kittyEventType)

            val calls = bridge.inputV3Calls
            bridge.forcedInputV3Failure = NativeBridgeResult.Failure(
                NativeTerminalStatus.UNSUPPORTED.code,
            )
            assertFalse(engine.submitKeyCode(
                KeyEvent.KEYCODE_ENTER,
                controlDown = false,
                altDown = false,
                shiftDown = true,
            ))
            bridge.forcedInputV3Failure = null
            assertFalse(engine.submitText("\u0080"))
            assertEquals(calls + 1, bridge.inputV3Calls)
            val blocked = requireNotNull(engine.inputDiagnostic.value)
            assertEquals(dev.termlink.terminal.api.TerminalInputDiagnosticOrigin.ANDROID_VALIDATION,
                blocked.origin)
            assertEquals("U+0080", blocked.codepoint)
            assertNull(dev.termlink.terminal.api.TerminalProtocolDiagnostic.parse(blocked.text))
            assertFalse(engine.submitText("A\u0081\u0082"))
            assertEquals("U+0081", engine.inputDiagnostic.value?.codepoint)

            assertTrue(engine.submitText("\u0001"))
            assertNull(engine.inputDiagnostic.value)
            assertEquals('a'.code, bridge.lastInputV3?.character)
            assertEquals(NATIVE_KITTY_MODIFIER_CONTROL, bridge.lastInputV3?.kittyModifiers)
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun orderedRemoteEventsUseHostEffectV2AndNeverUseLegacyApply() = runBlocking {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        try {
            engine.feed(1, ByteBuffer.wrap(byteArrayOf(1, 2)))
            assertTrue(bridge.lastHostEffectV2Batch?.events?.single() is NativeSourceEvent.Output)

            engine.resize(2, 100, 30)
            assertTrue(bridge.lastHostEffectV2Batch?.events?.single() is NativeSourceEvent.Resize)

            engine.advance(3)
            assertTrue(bridge.lastHostEffectV2Batch?.events?.single() is NativeSourceEvent.Advance)

            engine.reportGap(4)
            assertTrue(bridge.lastHostEffectV2Batch?.events?.single() is NativeSourceEvent.Gap)

            assertEquals(4, bridge.hostEffectV2Calls)
            assertEquals(0, bridge.applyCalls)
        } finally {
            engine.dispose()
        }
    }

    @Test
    fun visibleTextReadsPublishedFrameWhileNativeApplyIsBlocked() {
        val delegate = FakeNativeBridge()
        val enteredNativeApply = CountDownLatch(1)
        val releaseNativeApply = CountDownLatch(1)
        val bridge = object : NativeBridge by delegate {
            override fun applyBatchWithHostEffectsV2(
                handle: NativeBridgeHandle,
                batch: NativeEventBatch,
            ): NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2> {
                enteredNativeApply.countDown()
                check(releaseNativeApply.await(5, TimeUnit.SECONDS)) {
                    "test native reduction was not released"
                }
                return delegate.applyBatchWithHostEffectsV2(handle, batch)
            }
        }
        val engine = NativeTerminalEngine(TerminalSize(80, 24), bridge)
        val executor = Executors.newFixedThreadPool(2)
        val beforeFrame = engine.frames.value
        val before = frameText(beforeFrame)
        var feed: java.util.concurrent.Future<*>? = null
        try {
            feed = executor.submit {
                runBlocking { engine.feed(1, ByteBuffer.wrap("published\r\n".encodeToByteArray())) }
            }
            assertTrue(enteredNativeApply.await(5, TimeUnit.SECONDS))

            // The native writer owns the reducer lock here. The reader must use the last
            // immutable presentation instead of waiting for that in-flight reduction.
            val read = executor.submit<String> { engine.visibleText() }
            assertEquals(before, read.get(500, TimeUnit.MILLISECONDS))

            releaseNativeApply.countDown()
            requireNotNull(feed).get(5, TimeUnit.SECONDS)
            val after = engine.visibleText()
            val afterFrame = engine.frames.value
            assertTrue(afterFrame !== beforeFrame)
            assertTrue(afterFrame.presentationRevision > beforeFrame.presentationRevision)
            assertEquals(frameText(afterFrame), after)
        } finally {
            releaseNativeApply.countDown()
            runCatching { feed?.get(5, TimeUnit.SECONDS) }
            executor.shutdownNow()
            engine.dispose()
        }
        assertFailsWith<IllegalStateException> { engine.visibleText() }
    }

    @Test
    fun mirrorPtyReplyIsStrictlyDecodedAndDiscardedInsteadOfBecomingInput() = runBlocking {
        val bridge = FakeNativeBridge().apply {
            forcedHostEffectV2Result = NativeBridgeApplyWithHostEffectsV2(
                receipt = NativeBridgeApplyReceipt(
                    nextSourceSequence = 2,
                    presentationRevision = 2,
                ),
                hostEffectFrame = fakeHostEffectFrameV2(
                    records = listOf(
                        FakeHostEffectV2Record(
                            sourceSequence = 1,
                            sourceOffsetEnd = 4,
                            ordinal = 0,
                            payload = "\u001b[?25;1\$y".toByteArray(Charsets.US_ASCII),
                        ),
                    ),
                ),
            )
        }
        val engine = NativeTerminalEngine(
            initialSize = TerminalSize(80, 24),
            bridge = bridge,
        )
        val unexpectedInput = async(start = CoroutineStart.UNDISPATCHED) {
            engine.inputEvents.first()
        }
        try {
            engine.feed(1, ByteBuffer.wrap("query".toByteArray(Charsets.US_ASCII)))

            assertEquals(1, bridge.hostEffectV2Calls)
            assertEquals(0, bridge.applyCalls)
            assertFalse(unexpectedInput.isCompleted)
        } finally {
            unexpectedInput.cancelAndJoin()
            engine.dispose()
        }
    }

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

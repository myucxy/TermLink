package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertTrue

class NativeTerminalHandleTest {
    @Test
    fun closeIsIdempotentAndEveryOperationRejectsUseAfterClose() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)

        terminal.close()
        terminal.close()

        assertTrue(terminal.isClosed)
        assertEquals(1, bridge.destroyCalls)
        assertFailsWith<IllegalStateException> { terminal.render() }
        assertFailsWith<IllegalStateException> { terminal.feed(0, byteArrayOf(1)) }
        assertFailsWith<IllegalStateException> { terminal.exportCheckpoint() }
    }

    @Test
    fun batchesAreContinuousBoundedAndOwnTheirOutputBytes() {
        val bridge = FakeNativeBridge()
        val limits = NativeTerminalLimits(
            maxEventsPerBatch = 3,
            maxOutputBytesPerEvent = 4,
            maxOutputBytesPerBatch = 6,
        )
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1, limits)
        val first = byteArrayOf(1, 2, 3)
        val second = byteArrayOf(4, 5)
        val batch = NativeEventBatch.of(
            listOf(
                NativeSourceEvent.Output(1, first),
                NativeSourceEvent.Output(2, second),
            ),
            limits,
        )
        first.fill(9)
        second.fill(9)

        val receipt = terminal.apply(batch)

        assertEquals(3L, receipt.nextSourceSequence)
        assertEquals(3L, receipt.presentationRevision)
        assertContentEquals(byteArrayOf(4, 5), bridge.lastOutputBytes)
        assertFailsWith<IllegalArgumentException> { terminal.feed(4, byteArrayOf(6)) }
        assertEquals(1, bridge.applyCalls, "a local sequence failure must not enter the bridge")
        assertFailsWith<IllegalArgumentException> {
            terminal.resize(3, TerminalSize(513, 1))
        }
        assertEquals(1, bridge.applyCalls, "an oversized resize must not enter the bridge")
        terminal.resize(3, TerminalSize(10, 3))
        assertEquals(TerminalSize(10, 3), terminal.render().size)

        assertFailsWith<IllegalArgumentException> {
            NativeEventBatch.of(
                listOf(
                    NativeSourceEvent.Output(0, byteArrayOf(1)),
                    NativeSourceEvent.Output(1, byteArrayOf(1)),
                    NativeSourceEvent.Output(2, byteArrayOf(1)),
                    NativeSourceEvent.Output(3, byteArrayOf(1)),
                ),
                limits,
            )
        }
        assertFailsWith<IllegalArgumentException> {
            NativeSourceEvent.Output(4, byteArrayOf())
        }
        assertFailsWith<IllegalArgumentException> {
            terminal.feed(4, byteArrayOf())
        }
        assertEquals(2, bridge.applyCalls, "empty output must not enter the bridge")
        assertFailsWith<IllegalArgumentException> {
            terminal.feed(4, ByteArray(5))
        }
        terminal.close()
    }

    @Test
    fun fakeBatchFailureDoesNotCommitAPrefix() {
        val gapBridge = FakeNativeBridge()
        val allocation = assertIs<NativeBridgeResult.Success<NativeBridgeTerminal>>(
            gapBridge.create(TerminalSize(8, 2), 1),
        ).value
        val gapResult = gapBridge.applyBatch(
            allocation.handle,
            NativeEventBatch.of(
                listOf(
                    NativeSourceEvent.Resize(1, TerminalSize(20, 5)),
                    NativeSourceEvent.Gap(2),
                ),
            ),
        )

        val gap = assertIs<NativeBridgeResult.Failure>(gapResult)
        assertEquals(NativeTerminalStatus.GAP.code, gap.statusCode)
        assertEquals(
            FakeNativeState(
                nextSourceSequence = 1,
                presentationRevision = 1,
                size = TerminalSize(8, 2),
                logicalMicros = 0,
                failedClosed = true,
                checkpointEligible = true,
            ),
            gapBridge.onlyStateForTest(),
        )
        assertIs<NativeBridgeResult.Success<Unit>>(gapBridge.destroy(allocation.handle))

        val overflowBridge = FakeNativeBridge()
        val overflowTerminal = NativeTerminalHandle.create(
            overflowBridge,
            TerminalSize(8, 2),
            1,
        )
        val overflow = assertFailsWith<NativeTerminalException> {
            overflowTerminal.apply(
                listOf(
                    NativeSourceEvent.Advance(1, Long.MAX_VALUE),
                    NativeSourceEvent.Advance(2, 1),
                ),
            )
        }
        assertEquals(NativeTerminalStatus.TIME_OVERFLOW, overflow.status)
        assertEquals(1L, overflowTerminal.nextSourceSequence)
        assertEquals(1L, overflowTerminal.presentationRevision)

        // If the successful prefix above had leaked, this retry would itself overflow.
        overflowTerminal.advance(1, Long.MAX_VALUE)
        assertEquals(Long.MAX_VALUE, overflowBridge.onlyStateForTest().logicalMicros)
        overflowTerminal.close()
    }

    @Test
    fun rejectsMalformedAndOversizedRenderFrames() {
        val bridge = FakeNativeBridge()
        val limits = NativeTerminalLimits(maxRenderFrameBytes = 2_048)
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1, limits)

        bridge.forcedRenderFrame = NativeBridgeRenderFrame(payload = byteArrayOf())
        assertFailsWith<NativeTerminalContractException> { terminal.render() }

        bridge.forcedRenderFrame = NativeBridgeRenderFrame(payload = ByteArray(2_049))
        assertFailsWith<NativeTerminalContractException> { terminal.render() }

        bridge.forcedRenderFrame = NativeBridgeRenderFrame(
            payload = fakeRenderFrameV1(2, size = TerminalSize(8, 2)),
        )
        assertFailsWith<NativeTerminalContractException> { terminal.render() }

        bridge.forcedRenderFrame = NativeBridgeRenderFrame(
            payload = fakeRenderFrameV1(
                nextSourceSequence = 1,
                presentationRevision = 0,
                size = TerminalSize(8, 2),
            ),
        )
        assertFailsWith<NativeTerminalContractException> { terminal.render() }

        // Decode failures are detached: the handle counters stay intact and a later valid render
        // succeeds without reopening the terminal.
        bridge.forcedRenderFrame = null
        assertEquals(1L, terminal.nextSourceSequence)
        assertEquals(1L, terminal.presentationRevision)
        assertEquals(TerminalSize(8, 2), terminal.render().size)
        terminal.close()
    }

    @Test
    fun malformedScrollbackFrameExposesContentFreeFailureDiagnostics() {
        val bridge = FakeNativeBridge().apply {
            forcedRenderFrameV3 = NativeBridgeRenderFrame(byteArrayOf())
        }
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)

        val failure = assertFailsWith<NativeTerminalContractException> {
            terminal.renderV3AtScrollback(0)
        }

        assertEquals("renderV3AtScrollback.frameDecode", failure.terminalOperation)
        assertEquals("MALFORMED_BRIDGE_RESULT", failure.terminalStatusName)
        assertEquals(257, failure.terminalStatusCode)
        terminal.close()
    }

    @Test
    fun checkpointRoundTripsWithoutExposingMutableStorage() {
        val bridge = FakeNativeBridge()
        val original = NativeTerminalHandle.create(bridge, TerminalSize(12, 4), 1)
        assertEquals(1L, original.nextSourceSequence)
        original.feed(1, "prefix".encodeToByteArray())
        val checkpoint = original.exportCheckpoint()
        val encoded = checkpoint.copyPayload()
        encoded[0] = 0

        val restored = NativeTerminalHandle.importCheckpoint(bridge, checkpoint.copyPayload())

        assertEquals(original.nextSourceSequence, restored.nextSourceSequence)
        assertEquals(2L, restored.nextSourceSequence)
        assertEquals(original.presentationRevision, restored.presentationRevision)
        assertEquals(TerminalSize(12, 4), restored.render().size)
        assertFalse(checkpoint.copyPayload().contentEquals(encoded))
        original.close()
        restored.close()

        val boundedBridge = FakeNativeBridge()
        val bounded = NativeTerminalHandle.create(
            boundedBridge,
            TerminalSize(8, 2),
            1,
            NativeTerminalLimits(maxCheckpointBytes = 32),
        )
        boundedBridge.forcedCheckpoint = NativeBridgeCheckpoint(
            nextSourceSequence = 1,
            presentationRevision = 1,
            payload = ByteArray(33),
        )
        assertFailsWith<NativeTerminalContractException> { bounded.exportCheckpoint() }
        assertFailsWith<IllegalArgumentException> {
            NativeTerminalHandle.importCheckpoint(
                boundedBridge,
                ByteArray(33),
                NativeTerminalLimits(maxCheckpointBytes = 32),
            )
        }
        bounded.close()
    }

    @Test
    fun snapshotNotEligibleIsTemporaryUntilAnotherBatchCompletesTheBoundary() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)
        terminal.feed(1, byteArrayOf(0x1b))
        bridge.markCheckpointIneligibleUntilNextSuccessfulBatch()

        val pending = assertFailsWith<NativeTerminalException> {
            terminal.exportCheckpoint()
        }

        assertEquals(NativeTerminalStatus.SNAPSHOT_NOT_ELIGIBLE, pending.status)
        assertEquals(2L, terminal.nextSourceSequence)
        assertEquals(TerminalSize(8, 2), terminal.render().size)

        // The fixture treats bytes as opaque. A later successful batch only models that the native
        // parser reached an eligible boundary; Kotlin does not interpret the sequence.
        terminal.feed(2, byteArrayOf('c'.code.toByte()))
        val checkpoint = terminal.exportCheckpoint()
        assertEquals(3L, checkpoint.nextSourceSequence)
        terminal.close()
    }

    @Test
    fun synchronizedPresentationDeferredIsKnownAndDoesNotPoisonTheHandle() {
        val bridge = FakeNativeBridge().apply {
            forcedCheckpointFailure = NativeBridgeResult.Failure(
                NativeTerminalStatus.PRESENTATION_DEFERRED.code,
                "synchronized output is active",
            )
        }
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)

        val deferred = assertFailsWith<NativeTerminalException> {
            terminal.exportCheckpoint()
        }

        assertEquals(NativeTerminalStatus.PRESENTATION_DEFERRED, deferred.status)
        assertEquals(NativeTerminalStatus.PRESENTATION_DEFERRED.code, deferred.rawStatusCode)
        assertEquals(1L, terminal.nextSourceSequence)

        bridge.forcedCheckpointFailure = null
        val checkpoint = terminal.exportCheckpoint()
        assertEquals(1L, checkpoint.nextSourceSequence)
        assertEquals(TerminalSize(8, 2), terminal.render().size)
        terminal.close()
    }

    @Test
    fun applyFailuresUseAnExplicitRetryableWhitelist() {
        val retryable = listOf(
            NativeTerminalStatus.INVALID_ARGUMENT,
            NativeTerminalStatus.INVALID_DIMENSIONS,
            NativeTerminalStatus.LIMIT_EXCEEDED,
            NativeTerminalStatus.SEQUENCE_MISMATCH,
            NativeTerminalStatus.TIME_OVERFLOW,
        )
        retryable.forEach { status ->
            val bridge = FakeNativeBridge().apply {
                forcedApplyFailure = NativeBridgeResult.Failure(status.code)
            }
            val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)

            assertEquals(
                status,
                assertFailsWith<NativeTerminalException> {
                    terminal.feed(1, byteArrayOf(1))
                }.status,
            )
            bridge.forcedApplyFailure = null
            terminal.feed(1, byteArrayOf(1))
            assertEquals(2L, terminal.nextSourceSequence)
            terminal.close()
        }

        val poisonFailures = listOf(
            NativeTerminalStatus.UNSUPPORTED.code to NativeTerminalStatus.UNSUPPORTED,
            NativeTerminalStatus.FAILED_CLOSED.code to NativeTerminalStatus.FAILED_CLOSED,
            NativeTerminalStatus.GAP.code to NativeTerminalStatus.GAP,
            NativeTerminalStatus.PANIC.code to NativeTerminalStatus.PANIC,
            777 to NativeTerminalStatus.UNKNOWN,
            NativeTerminalStatus.BRIDGE_FAILURE.code to
                NativeTerminalStatus.MALFORMED_BRIDGE_RESULT,
        )
        poisonFailures.forEach { (rawCode, expectedStatus) ->
            val bridge = FakeNativeBridge().apply {
                forcedApplyFailure = NativeBridgeResult.Failure(rawCode)
            }
            val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)

            assertEquals(
                expectedStatus,
                assertFailsWith<NativeTerminalException> {
                    terminal.feed(1, byteArrayOf(1))
                }.status,
            )
            bridge.forcedApplyFailure = null
            assertFailsWith<IllegalStateException> { terminal.feed(1, byteArrayOf(1)) }
            terminal.close()
            terminal.close()
            assertEquals(1, bridge.destroyCalls)
        }
    }

    @Test
    fun copiesBridgeOwnedReturnBuffersBeforeExposure() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)
        val rawFrame = fakeRenderFrameV1(1, size = TerminalSize(8, 2))
        bridge.forcedRenderFrame = NativeBridgeRenderFrame(payload = rawFrame)
        val frame = terminal.render()
        rawFrame.fill(9)
        assertEquals(1L, frame.nextSourceSequence)
        assertEquals(1L, frame.presentationRevision)
        assertEquals(TerminalSize(8, 2), frame.size)
        assertEquals(" ", frame.renderFrame.graphemes.single().text)

        val rawCheckpoint = byteArrayOf(4, 5, 6)
        bridge.forcedCheckpoint = NativeBridgeCheckpoint(
            nextSourceSequence = 1,
            presentationRevision = 1,
            payload = rawCheckpoint,
        )
        val checkpoint = terminal.exportCheckpoint()
        rawCheckpoint.fill(9)
        assertContentEquals(byteArrayOf(4, 5, 6), checkpoint.copyPayload())
        terminal.close()
    }

    @Test
    fun fakeCanonicalFrameCarriesReverseVideoConcealAndStrikethrough() {
        val bridge = FakeNativeBridge()
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)
        bridge.forcedRenderFrame = NativeBridgeRenderFrame(
            payload = fakeRenderFrameV1(
                nextSourceSequence = 1,
                size = TerminalSize(8, 2),
                reverseVideo = true,
                conceal = true,
                strikethrough = true,
            ),
        )

        val frame = terminal.render().renderFrame

        assertTrue(frame.reverseVideo)
        assertTrue(frame.renditions.single().conceal)
        assertTrue(frame.renditions.single().strikethrough)
        terminal.close()
    }

    @Test
    fun rejectsAllocationCountersOutsideTheSupportedRange() {
        listOf(
            NativeBridgeTerminal(NativeBridgeHandle(41), Long.MAX_VALUE, 0),
            NativeBridgeTerminal(NativeBridgeHandle(42), 1, Long.MAX_VALUE),
            NativeBridgeTerminal(NativeBridgeHandle(43), 1, 0),
        ).forEach { malformed ->
            val bridge = FakeNativeBridge().apply { forcedCreateAllocation = malformed }

            assertFailsWith<NativeTerminalContractException> {
                NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)
            }
            assertEquals(1, bridge.destroyCalls)
        }
    }

    @Test
    fun stableStatusMappingPreservesKnownUnknownAndThrownBridgeFailures() {
        val gapBridge = FakeNativeBridge().apply {
            forcedApplyFailure = NativeBridgeResult.Failure(
                NativeTerminalStatus.GAP.code,
                "journal gap",
            )
        }
        val gapTerminal = NativeTerminalHandle.create(gapBridge, TerminalSize(8, 2), 1)
        val gap = assertFailsWith<NativeTerminalException> {
            gapTerminal.feed(1, byteArrayOf(1))
        }
        assertEquals(NativeTerminalStatus.GAP, gap.status)
        assertEquals(NativeTerminalStatus.GAP.code, gap.rawStatusCode)
        assertFailsWith<IllegalStateException> { gapTerminal.render() }
        gapTerminal.close()

        val unknownBridge = FakeNativeBridge().apply {
            forcedCreateFailure = NativeBridgeResult.Failure(777, "future status")
        }
        val unknown = assertFailsWith<NativeTerminalException> {
            NativeTerminalHandle.create(unknownBridge, TerminalSize(8, 2), 1)
        }
        assertEquals(NativeTerminalStatus.UNKNOWN, unknown.status)
        assertEquals(777, unknown.rawStatusCode)

        val throwingBridge = FakeNativeBridge().apply {
            throwOnApply = IllegalStateException("fixture bridge failed")
        }
        val throwingTerminal = NativeTerminalHandle.create(throwingBridge, TerminalSize(8, 2), 1)
        val failure = assertFailsWith<NativeTerminalException> {
            throwingTerminal.feed(1, byteArrayOf(1))
        }
        assertEquals(NativeTerminalStatus.BRIDGE_FAILURE, failure.status)
        assertFailsWith<IllegalStateException> { throwingTerminal.render() }
        throwingTerminal.close()
    }

    @Test
    fun mutatingJvmErrorPoisonsTheHandleAndCloseStillDestroysItOnce() {
        val bridge = FakeNativeBridge().apply {
            throwOnApply = AssertionError("native linkage failed after mutation became uncertain")
        }
        val terminal = NativeTerminalHandle.create(bridge, TerminalSize(8, 2), 1)

        val failure = assertFailsWith<NativeTerminalException> {
            terminal.feed(1, byteArrayOf(1))
        }
        assertEquals(NativeTerminalStatus.BRIDGE_FAILURE, failure.status)
        assertEquals(AssertionError::class, failure.cause!!::class)
        assertFailsWith<IllegalStateException> { terminal.render() }

        terminal.close()
        terminal.close()
        assertEquals(1, bridge.destroyCalls)
    }

    @Test
    fun rejectsAbiMismatchBeforeAllocatingAHandle() {
        val bridges = listOf(
            FakeNativeBridge().apply { abiVersion = 2 },
            FakeNativeBridge().apply { renderFrameFormatVersion = 2 },
            FakeNativeBridge().apply { maxRenderFrameBytes -= 1 },
            FakeNativeBridge().apply { mouseInputV1StructSize -= 1 },
        )

        bridges.forEach { bridge ->
            val failure = assertFailsWith<NativeTerminalException> {
                NativeTerminalHandle.create(bridge, TerminalSize(80, 24), 1)
            }

            assertEquals(NativeTerminalStatus.ABI_MISMATCH, failure.status)
            assertEquals(0, bridge.destroyCalls)
        }
    }
}

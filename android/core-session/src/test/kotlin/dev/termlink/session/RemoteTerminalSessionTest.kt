package dev.termlink.session

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.util.UUID
import kotlin.test.assertFailsWith
import kotlin.test.assertIs

@OptIn(ExperimentalCoroutinesApi::class)
class RemoteTerminalSessionTest {
    @Test
    fun feedsBeforeCumulativeAckAndDetectsDuplicateAndGap() = runTest {
        val log = mutableListOf<String>()
        val engine = RecordingEngine { sequence, _ ->
            log += "feed-start:$sequence"
            delay(10)
            log += "feed-end:$sequence"
        }
        val outbound = RecordingOutbound(log)
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)

        assertTrue(session.onConnected())
        val applied = session.accept(RemoteSessionEvent.Output(1, "ok".encodeToByteArray()))
        val duplicate = session.accept(RemoteSessionEvent.Output(1, "ignored".encodeToByteArray()))
        val gap = session.accept(RemoteSessionEvent.Output(3, "gap".encodeToByteArray()))

        assertEquals(EventAcceptance.Applied(1), applied)
        assertEquals(EventAcceptance.Duplicate(1), duplicate)
        assertEquals(EventAcceptance.HistoryGap(2, 3), gap)
        assertEquals(listOf("feed-start:1", "feed-end:1", "ack:1"), log)
        assertEquals(listOf(1L), outbound.acknowledgements)
        assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(1, engine.feeds.size)
        session.close()
    }

    @Test
    fun reconnectSubmitsOnlyTheHighestAppliedSequence() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)

        assertTrue(session.onConnected())
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        session.onDisconnected("network switch")
        assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        assertTrue(session.onConnected())

        assertEquals(listOf(0L, 1L), outbound.resumes)
        assertEquals(1L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun ackFailureKeepsAppliedSequenceForTheNextResume() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound().apply { failAck = true }
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1))),
        )
        val reconnecting = assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        assertEquals(1L, reconnecting.lastAckedSequence)

        outbound.failAck = false
        assertTrue(session.onConnected())
        assertEquals(listOf(0L, 1L), outbound.resumes)
        session.close()
    }

    @Test
    fun explicitGapDoesNotAdvanceAck() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))

        assertEquals(
            EventAcceptance.HistoryGap(2, 4),
            session.accept(RemoteSessionEvent.Gap(4, 2, 3)),
        )
        assertEquals(listOf(1L), outbound.acknowledgements)
        assertEquals(1L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun boundedRawReplayMustStartAtOneAndRebuildsTheEngine() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(2, byteArrayOf(9)))
        assertIs<RemoteSessionState.HistoryGap>(session.state.value)

        val last = session.resetAndReplay(
            listOf(
                RemoteSessionEvent.Output(1, byteArrayOf(1, 2)),
                RemoteSessionEvent.Resize(2, TerminalSize(100, 30)),
                RemoteSessionEvent.Title(3, "build"),
            ),
        )

        assertEquals(3L, last)
        assertEquals(1, engine.clearCount)
        assertEquals(listOf(1L), engine.feeds.map { it.first })
        assertArrayEquals(byteArrayOf(1, 2), engine.feeds.single().second)
        assertEquals(listOf(TerminalSize(100, 30)), engine.resizes)
        assertEquals("build", session.title.value)
        assertEquals(listOf(1L, 2L, 3L), outbound.acknowledgements)
        session.close()
    }

    @Test
    fun boundedRawReplayRejectsAStreamThatNeedsAnUnfrozenSnapshot() = runTest {
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(engine, RecordingOutbound(), backgroundScope)

        assertFailsWith<IllegalArgumentException> {
            session.resetAndReplay(listOf(RemoteSessionEvent.Output(7, byteArrayOf(7))))
        }
        assertEquals(0, engine.clearCount)
        assertTrue(engine.feeds.isEmpty())
        session.close()
    }

    @Test
    fun inputRequiresLiveLeaseAndEveryAcceptedInputGetsANewId() = runTest {
        val firstId = UUID.fromString("00000000-0000-0000-0000-000000000001")
        val secondId = UUID.fromString("00000000-0000-0000-0000-000000000002")
        val ids = ArrayDeque(listOf(firstId, secondId))
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            inputIdFactory = { ids.removeFirst() },
        )
        session.start()
        runCurrent()
        session.onConnected()

        engine.emitInput("blocked".encodeToByteArray())
        runCurrent()
        assertTrue(outbound.inputs.isEmpty())

        session.grantControl("lease-1", 1_000)
        engine.emitInput("one".encodeToByteArray())
        engine.emitInput("two".encodeToByteArray())
        runCurrent()

        assertEquals(listOf(firstId, secondId), outbound.inputs.map(RemoteInput::inputId))
        assertArrayEquals("one".encodeToByteArray(), outbound.inputs[0].bytes)
        assertArrayEquals("two".encodeToByteArray(), outbound.inputs[1].bytes)
        assertIs<RemoteSessionState.Controlling>(session.state.value)

        advanceTimeBy(1_000)
        runCurrent()
        assertIs<RemoteSessionState.ReadOnly>(session.state.value)
        assertTrue(engine.readOnlyState)
        engine.emitInput("expired".encodeToByteArray())
        runCurrent()
        assertEquals(2, outbound.inputs.size)
        session.close()
    }

    @Test
    fun failedInputIsNeverRetriedAndMovesSessionToReconnecting() = runTest {
        val inputId = UUID.fromString("00000000-0000-0000-0000-000000000003")
        val engine = RecordingEngine()
        val outbound = RecordingOutbound().apply { failInput = true }
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            inputIdFactory = { inputId },
        )
        session.onConnected()
        session.grantControl("lease", 10_000)

        val result = session.submitInput("do-not-retry".encodeToByteArray())

        assertEquals(InputSubmission.TransportFailed(inputId), result)
        assertEquals(1, outbound.inputAttempts)
        assertTrue(outbound.inputs.isEmpty())
        assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        session.close()
    }

    @Test
    fun terminalSizeEventsAreDistinctAndDebounced() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            resizeDebounceMillis = 150,
        )
        session.start()
        runCurrent()
        session.onConnected()

        engine.emitSize(TerminalSize(80, 25))
        advanceTimeBy(50)
        engine.emitSize(TerminalSize(100, 30))
        advanceTimeBy(149)
        assertTrue(outbound.sizes.isEmpty())
        advanceTimeBy(1)
        runCurrent()
        assertEquals(listOf(TerminalSize(100, 30)), outbound.sizes)

        engine.emitSize(TerminalSize(100, 30))
        advanceTimeBy(200)
        runCurrent()
        assertEquals(1, outbound.sizes.size)
        session.close()
    }

    @Test
    fun exitedSessionIsReadOnlyAndRejectsFurtherInputAndOutput() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
        )
        session.onConnected()
        session.grantControl("lease", 10_000)

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Lifecycle(1, RemoteLifecycle.ENDED, 17)),
        )
        assertEquals(InputSubmission.RejectedExited, session.submitInput(byteArrayOf(1)))
        assertEquals(
            EventAcceptance.IgnoredAfterExit,
            session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2))),
        )
        val exited = assertIs<RemoteSessionState.Exited>(session.state.value)
        assertEquals(17, exited.exitCode)
        assertTrue(engine.readOnlyState)
        assertTrue(outbound.inputs.isEmpty())
        session.close()
    }

    @Test
    fun endedLifecycleRemainsExitedWhenItsFinalAckCannotBeSent() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound().apply { failAck = true }
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Lifecycle(1, RemoteLifecycle.ENDED, 17)),
        )
        val exited = assertIs<RemoteSessionState.Exited>(session.state.value)
        assertEquals(1L, exited.lastAckedSequence)
        assertEquals(17, exited.exitCode)
        assertTrue(engine.readOnlyState)
        session.close()
    }

    private class RecordingEngine(
        private val onFeed: suspend (Long, ByteArray) -> Unit = { _, _ -> },
    ) : TerminalEngine {
        private val inputs = MutableSharedFlow<ByteArray>(extraBufferCapacity = 8)
        private val sizes = MutableSharedFlow<TerminalSize>(extraBufferCapacity = 8)
        override val inputEvents: Flow<ByteArray> = inputs
        override val sizeEvents: Flow<TerminalSize> = sizes
        val feeds = mutableListOf<Pair<Long, ByteArray>>()
        val resizes = mutableListOf<TerminalSize>()
        var readOnlyState = true
        var clearCount = 0
        var disposed = false

        suspend fun emitInput(bytes: ByteArray) {
            inputs.emit(bytes)
        }

        suspend fun emitSize(size: TerminalSize) {
            sizes.emit(size)
        }

        override suspend fun feed(seq: Long, bytes: ByteBuffer) {
            val copy = ByteArray(bytes.remaining()).also(bytes::get)
            onFeed(seq, copy)
            feeds += seq to copy
        }

        override fun resize(columns: Int, rows: Int) {
            resizes += TerminalSize(columns, rows)
        }

        override fun applyTheme(theme: TerminalThemeV1) = Unit

        override fun setReadOnly(readOnly: Boolean) {
            readOnlyState = readOnly
        }

        override fun clear() {
            clearCount++
            feeds.clear()
        }

        override fun dispose() {
            disposed = true
        }
    }

    private class RecordingOutbound(
        private val log: MutableList<String>? = null,
    ) : RemoteSessionOutbound {
        val resumes = mutableListOf<Long>()
        val acknowledgements = mutableListOf<Long>()
        val inputs = mutableListOf<RemoteInput>()
        val sizes = mutableListOf<TerminalSize>()
        var inputAttempts = 0
        var failInput = false
        var failAck = false

        override suspend fun resume(lastAckedSequence: Long) {
            resumes += lastAckedSequence
        }

        override suspend fun acknowledge(highestContiguousSequence: Long) {
            if (failAck) throw IllegalStateException("ACK offline")
            acknowledgements += highestContiguousSequence
            log?.add("ack:$highestContiguousSequence")
        }

        override suspend fun sendInput(input: RemoteInput) {
            inputAttempts++
            if (failInput) throw IllegalStateException("offline")
            inputs += input.copy(bytes = input.bytes.copyOf())
        }

        override suspend fun sendResize(size: TerminalSize) {
            sizes += size
        }
    }
}

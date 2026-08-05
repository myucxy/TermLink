package dev.termlink.session

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
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
        runCurrent()

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
        runCurrent()
        session.onDisconnected("network switch")
        assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        assertTrue(session.onConnected())

        assertEquals(listOf(0L, 1L), outbound.resumes)
        assertEquals(1L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun restoredRendererStartsAtItsDurableCursorWithoutFeedingDuplicates() = runTest {
        val engine = RecordingEngine()
        engine.feed(41, ByteBuffer.wrap(byteArrayOf(1)))
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = outbound,
            parentScope = backgroundScope,
            initialAcknowledgedSequence = 41,
        )

        assertTrue(session.onConnected())
        assertEquals(listOf(41L), outbound.resumes)
        assertEquals(
            EventAcceptance.Duplicate(41),
            session.accept(RemoteSessionEvent.Output(41, byteArrayOf(1))),
        )
        assertEquals(
            EventAcceptance.Applied(42),
            session.accept(RemoteSessionEvent.Output(42, byteArrayOf(2))),
        )
        runCurrent()
        assertEquals(listOf(42L), outbound.acknowledgements)
        assertEquals(listOf(41L, 42L), engine.feeds.map { it.first })
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
        runCurrent()
        val reconnecting = assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        assertEquals(1L, reconnecting.lastAckedSequence)

        outbound.failAck = false
        assertTrue(session.onConnected())
        assertEquals(listOf(0L, 1L), outbound.resumes)
        session.close()
    }

    @Test
    fun persistenceFailureResumesDurableCursorAndDuplicateRetriggersCommitWithoutRefed() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound().apply { failPersist = true }
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1))),
        )
        runCurrent()
        val reconnecting = assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        assertEquals(0L, reconnecting.lastAckedSequence)
        assertTrue(outbound.acknowledgements.isEmpty())

        outbound.failPersist = false
        assertTrue(session.onConnected())
        assertEquals(
            EventAcceptance.Duplicate(1),
            session.accept(RemoteSessionEvent.Output(1, byteArrayOf(9))),
        )
        runCurrent()

        assertEquals(listOf(0L, 0L), outbound.resumes)
        assertEquals(listOf(1L), outbound.persistedAcknowledgements)
        assertEquals(listOf(1L), outbound.acknowledgements)
        assertEquals(listOf(1L), engine.feeds.map { it.first })
        assertEquals(1L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun tenThousandAppliedEventsCollapseBehindOneBlockedDurableCommit() = runTest {
        val firstCommitStarted = CompletableDeferred<Unit>()
        val releaseFirstCommit = CompletableDeferred<Unit>()
        val persisted = mutableListOf<Long>()
        val published = mutableListOf<Long>()
        val outbound = object : RemoteSessionOutbound {
            override suspend fun resume(lastAckedSequence: Long) = Unit

            override suspend fun persistAcknowledgement(highestContiguousSequence: Long): Long {
                persisted += highestContiguousSequence
                if (persisted.size == 1) {
                    firstCommitStarted.complete(Unit)
                    releaseFirstCommit.await()
                }
                return highestContiguousSequence
            }

            override suspend fun acknowledge(highestContiguousSequence: Long) {
                published += highestContiguousSequence
            }

            override suspend fun sendInput(input: RemoteInput) = Unit
            override suspend fun sendResize(size: TerminalSize) = Unit
        }
        val session = RemoteTerminalSession(RecordingEngine(), outbound, backgroundScope)

        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()
        firstCommitStarted.await()
        for (sequence in 2L..10_000L) {
            session.accept(RemoteSessionEvent.Output(sequence, byteArrayOf(1)))
        }
        releaseFirstCommit.complete(Unit)
        runCurrent()

        assertEquals(listOf(1L, 10_000L), persisted)
        assertEquals(listOf(1L, 10_000L), published)
        assertEquals(10_000L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun explicitGapDoesNotAdvanceAck() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()

        assertEquals(
            EventAcceptance.HistoryGap(2, 4),
            session.accept(RemoteSessionEvent.Gap(4, 2, 3)),
        )
        assertEquals(listOf(1L), outbound.acknowledgements)
        assertEquals(1L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun overlappingGapFreezesOnlyTheSuffixNotAlreadyApplied() = runTest {
        val session = RemoteTerminalSession(RecordingEngine(), RecordingOutbound(), backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2)))

        assertEquals(
            EventAcceptance.HistoryGap(3, 4),
            session.accept(RemoteSessionEvent.Gap(4, 2, 3)),
        )
        val gap = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(3L, gap.expectedSequence)
        assertEquals(4L, gap.receivedSequence)
        session.close()
    }

    @Test
    fun replayedGapMarkerIsAcknowledgedAfterHistoryFilledItsRange() = runTest {
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(RecordingEngine(), outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()
        val activeGap = assertIs<RemoteSessionState.HistoryGap>(
            session.accept(RemoteSessionEvent.Gap(4, 2, 3)).let { session.state.value },
        )
        session.recoverFromHistory(
            events = listOf(
                RemoteSessionEvent.Output(1, byteArrayOf(1)),
                RemoteSessionEvent.Output(2, byteArrayOf(2)),
                RemoteSessionEvent.Output(3, byteArrayOf(3)),
            ),
            expectedGap = activeGap,
            persist = {},
            publish = {},
        )

        assertEquals(
            EventAcceptance.Applied(4),
            session.accept(RemoteSessionEvent.Gap(4, 2, 3)),
        )
        runCurrent()
        assertEquals(4L, session.state.value.lastAckedSequence)
        assertTrue(session.state.value !is RemoteSessionState.HistoryGap)
        session.close()
    }

    @Test
    fun historyGapFreezesSubsequentEventsUntilFullRecovery() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()
        session.accept(RemoteSessionEvent.Gap(4, 2, 3))

        assertEquals(
            EventAcceptance.HistoryGap(2, 4),
            session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2))),
        )
        assertEquals(
            EventAcceptance.HistoryGap(2, 4),
            session.accept(RemoteSessionEvent.Lifecycle(5, RemoteLifecycle.ENDED, 17)),
        )

        val gap = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(1L, gap.lastAckedSequence)
        assertEquals(listOf(1L), engine.feeds.map { it.first })
        assertEquals(listOf(1L), outbound.acknowledgements)
        session.close()
    }

    @Test
    fun historyGapSurvivesDisconnectAndReconnectAndCannotGainControl() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.HistoryGap(1, 3),
            session.accept(RemoteSessionEvent.Gap(3, 1, 2)),
        )
        session.onDisconnected("replay queue closed")
        assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertTrue(session.onConnected())
        assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(listOf(0L), outbound.resumes)
        assertFailsWith<IllegalStateException> {
            session.grantControl("lease", Long.MAX_VALUE)
        }
        assertEquals(
            InputSubmission.RejectedNoLease,
            session.submitInput("blocked".encodeToByteArray()),
        )
        assertTrue(engine.readOnlyState)
        session.close()
    }

    @Test
    fun orderedInputResultBetweenOutputsDoesNotCreateHistoryGap() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1))),
        )
        assertEquals(
            EventAcceptance.Applied(2),
            session.accept(
                RemoteSessionEvent.InputResult(
                    sequence = 2,
                    inputId = UUID.fromString("00000000-0000-0000-0000-000000000021"),
                    accepted = true,
                ),
            ),
        )
        assertEquals(
            EventAcceptance.Applied(3),
            session.accept(RemoteSessionEvent.Output(3, byteArrayOf(3))),
        )
        runCurrent()
        assertEquals(3L, session.state.value.lastAckedSequence)
        assertTrue(session.state.value !is RemoteSessionState.HistoryGap)
        assertEquals(listOf(3L), outbound.acknowledgements)
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
        runCurrent()

        assertEquals(3L, last)
        assertEquals(1, engine.clearCount)
        assertEquals(listOf(1L), engine.feeds.map { it.first })
        assertArrayEquals(byteArrayOf(1, 2), engine.feeds.single().second)
        assertEquals(listOf(TerminalSize(100, 30)), engine.resizes)
        assertEquals("build", session.title.value)
        assertEquals(listOf(3L), outbound.acknowledgements)
        session.close()
    }

    @Test
    fun boundedRawReplayCanDeferPerEventAcknowledgementsForOneCumulativeAck() = runTest {
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(RecordingEngine(), outbound, backgroundScope)

        val last = session.resetAndReplay(
            listOf(
                RemoteSessionEvent.Output(1, byteArrayOf(1)),
                RemoteSessionEvent.Output(2, byteArrayOf(2)),
            ),
            acknowledgeEach = false,
        )
        runCurrent()

        assertEquals(2L, last)
        assertEquals(listOf(2L), outbound.acknowledgements)
        assertEquals(2L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun failedRawReplayMidFeedClearsRendererLeavesReadOnlyGapAndCanRetry() = runTest {
        var failSecondFeed = true
        val engine = RecordingEngine { sequence, _ ->
            if (sequence == 2L && failSecondFeed) {
                failSecondFeed = false
                throw IllegalStateException("renderer feed failed")
            }
        }
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        val replay = listOf(
            RemoteSessionEvent.Output(1, byteArrayOf(1)),
            RemoteSessionEvent.Output(2, byteArrayOf(2)),
            RemoteSessionEvent.Output(3, byteArrayOf(3)),
        )

        assertFailsWith<IllegalStateException> {
            session.resetAndReplay(replay)
        }
        runCurrent()

        val failed = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(0L, failed.lastAckedSequence)
        assertEquals(1L, failed.expectedSequence)
        assertEquals(4L, failed.receivedSequence)
        assertTrue(engine.readOnlyState)
        assertEquals(2, engine.clearCount)
        assertTrue(engine.feeds.isEmpty())
        assertTrue(outbound.acknowledgements.isEmpty())

        assertEquals(3L, session.resetAndReplay(replay))
        runCurrent()

        assertEquals(listOf(1L, 2L, 3L), engine.feeds.map { it.first })
        assertEquals(listOf(3L), outbound.acknowledgements)
        assertEquals(3L, session.state.value.lastAckedSequence)
        assertTrue(session.state.value !is RemoteSessionState.HistoryGap)
        session.close()
    }

    @Test
    fun failedHistoryRecoveryKeepsOriginalGapAndCanRetryAtomically() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()
        session.accept(RemoteSessionEvent.Gap(4, 2, 3))
        val replay = listOf(
            RemoteSessionEvent.Output(1, byteArrayOf(1)),
            RemoteSessionEvent.Output(2, byteArrayOf(2)),
            RemoteSessionEvent.Output(3, byteArrayOf(3)),
        )

        assertFailsWith<IllegalStateException> {
            session.recoverFromHistory(
                replay,
                persist = { throw IllegalStateException("durable ACK unavailable") },
                publish = {},
            )
        }
        val failed = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(0L, failed.lastAckedSequence)
        assertEquals(1L, failed.expectedSequence)
        assertEquals(4L, failed.receivedSequence)
        assertTrue(engine.feeds.isEmpty())
        assertTrue(engine.readOnlyState)

        val commits = mutableListOf<Long>()
        assertEquals(
            3L,
            session.recoverFromHistory(
                replay,
                persist = { commits += it },
                publish = {},
            ),
        )
        assertEquals(listOf(3L), commits)
        assertEquals(3L, session.state.value.lastAckedSequence)
        assertTrue(session.state.value !is RemoteSessionState.HistoryGap)
        session.close()
    }

    @Test
    fun historyRecoveryAcceptsPrefixAckCommittedAfterGapSnapshot() = runTest {
        val persistStarted = CompletableDeferred<Unit>()
        val releasePersist = CompletableDeferred<Unit>()
        val outbound = RecordingOutbound(
            onPersist = { sequence ->
                if (sequence == 1L) {
                    persistStarted.complete(Unit)
                    releasePersist.await()
                }
            },
        )
        val session = RemoteTerminalSession(RecordingEngine(), outbound, backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        persistStarted.await()
        session.accept(RemoteSessionEvent.Gap(4, 2, 3))
        val observedGap = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(0L, observedGap.lastAckedSequence)

        val durableRecovery = mutableListOf<Long>()
        val publishedRecovery = mutableListOf<Long>()
        val recovery = async {
            session.recoverFromHistory(
                events = listOf(
                    RemoteSessionEvent.Output(1, byteArrayOf(1)),
                    RemoteSessionEvent.Output(2, byteArrayOf(2)),
                    RemoteSessionEvent.Output(3, byteArrayOf(3)),
                ),
                expectedGap = observedGap,
                persist = { durableRecovery += it },
                publish = { publishedRecovery += it },
            )
        }

        releasePersist.complete(Unit)
        assertEquals(3L, recovery.await())
        assertEquals(listOf(1L), outbound.persistedAcknowledgements)
        assertEquals(listOf(1L), outbound.acknowledgements)
        assertEquals(listOf(3L), durableRecovery)
        assertEquals(listOf(3L), publishedRecovery)
        assertEquals(3L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun partialHistoryRecoveryCannotClearTheActiveGap() = runTest {
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(engine, RecordingOutbound(), backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()
        session.accept(RemoteSessionEvent.Gap(4, 2, 3))

        assertFailsWith<IllegalArgumentException> {
            session.recoverFromHistory(
                listOf(
                    RemoteSessionEvent.Output(1, byteArrayOf(1)),
                    RemoteSessionEvent.Output(2, byteArrayOf(2)),
                ),
                persist = { error("partial replay must not be persisted") },
                publish = { error("partial replay must not be published") },
            )
        }

        val gap = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(1L, gap.lastAckedSequence)
        assertEquals(2L, gap.expectedSequence)
        assertEquals(4L, gap.receivedSequence)
        assertEquals(listOf(1L), engine.feeds.map { it.first })
        assertTrue(engine.readOnlyState)
        session.close()
    }

    @Test
    fun durableHistoryRecoveryKeepsCursorWhenPublicationFails() = runTest {
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(engine, RecordingOutbound(), backgroundScope)
        session.onConnected()
        session.accept(RemoteSessionEvent.Gap(3, 1, 2))
        val durable = mutableListOf<Long>()

        assertFailsWith<IllegalStateException> {
            session.recoverFromHistory(
                listOf(
                    RemoteSessionEvent.Output(1, byteArrayOf(1)),
                    RemoteSessionEvent.Output(2, byteArrayOf(2)),
                ),
                persist = { durable += it },
                publish = { throw IllegalStateException("socket closed") },
            )
        }

        assertEquals(listOf(2L), durable)
        val reconnecting = assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        assertEquals(2L, reconnecting.lastAckedSequence)
        assertEquals(listOf(1L, 2L), engine.feeds.map { it.first })
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
    fun terminalSizeIsBufferedUntilControlLeaseIsGranted() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            resizeDebounceMillis = 150,
            clockMillis = { testScheduler.currentTime },
        )
        session.start()
        runCurrent()
        session.onConnected()

        engine.emitSize(TerminalSize(100, 30))
        advanceTimeBy(150)
        runCurrent()

        assertTrue(outbound.sizes.isEmpty())
        assertIs<RemoteSessionState.Live>(session.state.value)

        session.grantControl("lease", 10_000)

        assertEquals(listOf(TerminalSize(100, 30)), outbound.sizes)
        session.close()
    }

    @Test
    fun controllingLeaseRenewsEveryConfiguredIntervalAndStopsOnRevoke() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            leaseRenewalIntervalMillis = 1_000,
        )
        session.onConnected()
        session.grantControl("lease", 10_000, leaseVersion = 7)

        advanceTimeBy(999)
        runCurrent()
        assertTrue(outbound.renewals.isEmpty())
        advanceTimeBy(1)
        runCurrent()
        assertEquals(listOf("lease" to 7L), outbound.renewals)

        session.revokeControl()
        advanceTimeBy(2_000)
        runCurrent()
        assertEquals(listOf("lease" to 7L), outbound.renewals)
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

        assertEquals(InputSubmission.DeliveryUnknown(inputId), result)
        assertEquals(1, outbound.inputAttempts)
        assertTrue(outbound.inputs.isEmpty())
        assertIs<RemoteSessionState.Reconnecting>(session.state.value)
        session.close()
    }

    @Test
    fun authoritativeInputRejectionDoesNotBreakAHealthyControlLease() = runTest {
        val inputId = UUID.fromString("00000000-0000-0000-0000-000000000004")
        val outbound = object : RemoteSessionOutbound {
            override suspend fun resume(lastAckedSequence: Long) = Unit
            override suspend fun acknowledge(highestContiguousSequence: Long) = Unit
            override suspend fun sendResize(size: TerminalSize) = Unit
            override suspend fun sendInput(input: RemoteInput) {
                throw RemoteInputRejectedException("INPUT_SEQUENCE_REJECTED")
            }
        }
        val session = RemoteTerminalSession(
            RecordingEngine(),
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            inputIdFactory = { inputId },
        )
        session.onConnected()
        session.grantControl("lease", 10_000)

        assertEquals(
            InputSubmission.RejectedByRemote(inputId, "INPUT_SEQUENCE_REJECTED"),
            session.submitInput(byteArrayOf(1)),
        )
        assertIs<RemoteSessionState.Controlling>(session.state.value)
        session.close()
    }

    @Test
    fun inputResultCanArriveWhileInputTransportIsAwaitingAcceptance() = runTest {
        val inputId = UUID.fromString("00000000-0000-0000-0000-000000000031")
        lateinit var session: RemoteTerminalSession
        val outbound = object : RemoteSessionOutbound {
            override suspend fun resume(lastAckedSequence: Long) = Unit
            override suspend fun acknowledge(highestContiguousSequence: Long) = Unit
            override suspend fun sendResize(size: TerminalSize) = Unit
            override suspend fun sendInput(input: RemoteInput) {
                assertEquals(inputId, input.inputId)
                assertEquals(
                    EventAcceptance.Applied(1),
                    session.accept(RemoteSessionEvent.InputResult(1, inputId, accepted = true)),
                )
            }
        }
        val sessionEngine = RecordingEngine()
        session = RemoteTerminalSession(
            sessionEngine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            inputIdFactory = { inputId },
        )
        session.onConnected()
        session.grantControl("lease", 10_000)

        assertEquals(InputSubmission.Sent(inputId), session.submitInput(byteArrayOf(1)))
        runCurrent()
        assertEquals(1L, session.state.value.lastAckedSequence)
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
            clockMillis = { testScheduler.currentTime },
        )
        session.start()
        runCurrent()
        session.onConnected()
        session.grantControl("lease", 10_000)

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
        runCurrent()
        val exited = assertIs<RemoteSessionState.Exited>(session.state.value)
        assertEquals(1L, exited.lastAckedSequence)
        assertEquals(17, exited.exitCode)
        assertTrue(engine.readOnlyState)
        session.close()
    }

    @Test
    fun legacyEndedLifecycleDoesNotPretendTheShellExitedSuccessfully() = runTest {
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(engine, RecordingOutbound(), backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Lifecycle(1, RemoteLifecycle.ENDED)),
        )
        val exited = assertIs<RemoteSessionState.Exited>(session.state.value)
        assertEquals(null, exited.exitCode)
        assertTrue(engine.readOnlyState)
        session.close()
    }

    @Test
    fun closeRejectsFurtherSessionWorkBeforeEngineCanBeReused() = runTest {
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(engine, RecordingOutbound(), backgroundScope)
        session.onConnected()

        session.close()

        assertTrue(engine.disposed)
        assertFalse(session.onConnected())
        assertFailsWith<IllegalStateException> {
            session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        }
        assertFailsWith<IllegalStateException> {
            session.submitInput(byteArrayOf(1))
        }
        assertFailsWith<IllegalStateException> {
            session.grantControl("lease", Long.MAX_VALUE)
        }
        assertTrue(engine.feeds.isEmpty())
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
        private val onPersist: suspend (Long) -> Unit = {},
    ) : RemoteSessionOutbound {
        val resumes = mutableListOf<Long>()
        val persistedAcknowledgements = mutableListOf<Long>()
        val acknowledgements = mutableListOf<Long>()
        val inputs = mutableListOf<RemoteInput>()
        val sizes = mutableListOf<TerminalSize>()
        val renewals = mutableListOf<Pair<String, Long>>()
        var inputAttempts = 0
        var failInput = false
        var failPersist = false
        var failAck = false

        override suspend fun resume(lastAckedSequence: Long) {
            resumes += lastAckedSequence
        }

        override suspend fun persistAcknowledgement(highestContiguousSequence: Long): Long {
            if (failPersist) throw IllegalStateException("ACK persistence offline")
            onPersist(highestContiguousSequence)
            persistedAcknowledgements += highestContiguousSequence
            return highestContiguousSequence
        }

        override suspend fun resetAcknowledgement() {
            persistedAcknowledgements.clear()
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

        override suspend fun renewLease(leaseId: String, leaseVersion: Long): RemoteControlLease? {
            renewals += leaseId to leaseVersion
            return null
        }
    }
}

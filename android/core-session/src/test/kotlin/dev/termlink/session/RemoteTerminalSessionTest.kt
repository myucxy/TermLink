package dev.termlink.session

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalInput
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
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
    fun presentationRevisionRegressionRequiresAuthenticatedResync() = runTest {
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(engine, RecordingOutbound(), backgroundScope)
        session.onConnected()

        assertEquals(
            EventAcceptance.Applied(1),
            session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1), 1, 7)),
        )
        assertEquals(
            EventAcceptance.HistoryGap(2, 2),
            session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2), 2, 6)),
        )
        assertEquals(1, engine.feeds.size)
        assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        session.close()
    }

    @Test
    fun semanticInputRetainsTypedAndLegacyRepresentations() = runTest {
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(RecordingEngine(), outbound, backgroundScope)
        session.onConnected()
        session.grantControl("lease", Long.MAX_VALUE)

        val typed = TerminalInput.Text("echo 中文\r")
        assertIs<InputSubmission.Sent>(session.submitInput(typed))
        assertEquals(1, outbound.inputs.size)
        assertEquals(typed, outbound.inputs.single().typedInput)
        assertArrayEquals("echo 中文\r".encodeToByteArray(), outbound.inputs.single().bytes)
        session.close()
    }

    @Test
    fun titlePresenceDistinguishesUnpublishedFromExplicitClear() = runTest {
        val session = RemoteTerminalSession(RecordingEngine(), RecordingOutbound(), backgroundScope)

        assertEquals(null, session.title.value)
        session.onConnected()
        assertEquals(EventAcceptance.Applied(1), session.accept(RemoteSessionEvent.Title(1, "build")))
        assertEquals("build", session.title.value)
        assertEquals(EventAcceptance.Applied(2), session.accept(RemoteSessionEvent.Title(2, "")))
        assertEquals("", session.title.value)
        session.close()
    }

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
            initialRendererState = RendererBootstrapState.FullPrefix(41, rendererGeneration = 1),
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
    fun restoredTailWindowContinuesFromItsEndWithoutRequestingTheOmittedPrefix() = runTest {
        val engine = RecordingEngine()
        engine.feed(40, ByteBuffer.wrap(byteArrayOf(1)))
        engine.feed(41, ByteBuffer.wrap(byteArrayOf(2)))
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = outbound,
            parentScope = backgroundScope,
            initialRendererState = RendererBootstrapState.TailWindow(
                startSequence = 40,
                sequence = 41,
                rendererGeneration = 1,
            ),
        )

        assertTrue(session.onConnected())
        assertEquals(listOf(41L), outbound.resumes)
        assertEquals(
            EventAcceptance.Applied(42),
            session.accept(RemoteSessionEvent.Output(42, byteArrayOf(3))),
        )
        runCurrent()
        assertEquals(listOf(42L), outbound.acknowledgements)
        assertEquals(listOf(40L, 41L, 42L), engine.feeds.map { it.first })
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
    fun acknowledgementShutdownWaitsUntilANonCancellableDurableWriterStops() = runTest {
        val commitStarted = CompletableDeferred<Unit>()
        val releaseCommit = CompletableDeferred<Unit>()
        val commitFinished = CompletableDeferred<Unit>()
        val outbound = RecordingOutbound(
            onPersist = {
                withContext(NonCancellable) {
                    commitStarted.complete(Unit)
                    releaseCommit.await()
                    commitFinished.complete(Unit)
                }
            },
        )
        val session = RemoteTerminalSession(RecordingEngine(), outbound, backgroundScope)

        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        runCurrent()
        commitStarted.await()
        session.close()
        val stopped = async { session.awaitAcknowledgementWriterStopped() }
        runCurrent()

        assertFalse(stopped.isCompleted)
        releaseCommit.complete(Unit)
        stopped.await()
        assertTrue(commitFinished.isCompleted)
    }

    @Test
    fun tenThousandAppliedEventsCollapseBehindOneBlockedDurableCommit() = runTest {
        val firstCommitStarted = CompletableDeferred<Unit>()
        val releaseFirstCommit = CompletableDeferred<Unit>()
        val persisted = mutableListOf<Long>()
        val published = mutableListOf<Long>()
        val outbound = object : RemoteSessionOutbound {
            override suspend fun resume(lastAckedSequence: Long) = Unit

            override suspend fun persistAcknowledgement(
                expectedRendererGeneration: Long,
                highestContiguousSequence: Long,
            ): DurableAckCommit {
                persisted += highestContiguousSequence
                if (persisted.size == 1) {
                    firstCommitStarted.complete(Unit)
                    releaseFirstCommit.await()
                }
                return DurableAckCommit(highestContiguousSequence, expectedRendererGeneration)
            }

            override suspend fun acknowledge(highestContiguousSequence: Long) {
                published += highestContiguousSequence
            }

            override suspend fun resetAcknowledgement(expectedRendererGeneration: Long) =
                DurableAckCommit(0, expectedRendererGeneration + 1)

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
            persist = { generation, sequence -> DurableAckCommit(sequence, generation) },
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
    fun liveRendererFailureEntersHistoryGapAndRecoveryIncludesTheFailedEvent() = runTest {
        var failSecondFeed = true
        val engine = RecordingEngine { sequence, _ ->
            if (sequence == 2L && failSecondFeed) {
                failSecondFeed = false
                throw IllegalStateException("renderer feed failed")
            }
        }
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(engine, outbound, backgroundScope)
        session.onConnected()

        assertEquals(EventAcceptance.Applied(1), session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1))))
        runCurrent()
        assertEquals(1L, session.state.value.lastAckedSequence)

        assertFailsWith<IllegalStateException> {
            session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2)))
        }

        val gap = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(1L, gap.lastAckedSequence)
        assertEquals(2L, gap.expectedSequence)
        assertEquals(3L, gap.receivedSequence)
        assertTrue(engine.readOnlyState)
        assertEquals(listOf(1L), engine.feeds.map { it.first })
        assertEquals(listOf(1L), outbound.acknowledgements)

        assertEquals(
            2L,
            session.recoverFromHistory(
                events = listOf(
                    RemoteSessionEvent.Output(1, byteArrayOf(1)),
                    RemoteSessionEvent.Output(2, byteArrayOf(2)),
                ),
                expectedGap = gap,
                persist = { generation, sequence -> DurableAckCommit(sequence, generation) },
                publish = {},
            ),
        )
        assertEquals(listOf(1L, 2L), engine.feeds.map { it.first })
        assertEquals(2L, session.state.value.lastAckedSequence)
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
                persist = { _, _ -> throw IllegalStateException("durable ACK unavailable") },
                publish = {},
            )
        }
        val failed = assertIs<RemoteSessionState.HistoryGap>(session.state.value)
        assertEquals(1L, failed.lastAckedSequence)
        assertEquals(1L, failed.expectedSequence)
        assertEquals(4L, failed.receivedSequence)
        assertTrue(engine.feeds.isEmpty())
        assertTrue(engine.readOnlyState)
        assertEquals(0, outbound.resetCount)

        val commits = mutableListOf<Long>()
        assertEquals(
            3L,
            session.recoverFromHistory(
                replay,
                persist = { generation, sequence ->
                    commits += sequence
                    DurableAckCommit(sequence, generation)
                },
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
                persist = { generation, sequence ->
                    durableRecovery += sequence
                    DurableAckCommit(sequence, generation)
                },
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
                persist = { _, _ -> error("partial replay must not be persisted") },
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
                persist = { generation, sequence ->
                    durable += sequence
                    DurableAckCommit(sequence, generation)
                },
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
    fun terminalSizeIsNeverSentWhenControlLeaseIsGranted() = runTest {
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

        assertTrue(outbound.sizes.isEmpty())
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
    fun transientRenewalFailureKeepsControlAndALaterSuccessExtendsTheLease() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        var attempts = 0
        outbound.onRenewLease = { leaseId, leaseVersion ->
            attempts += 1
            if (attempts == 1) throw IllegalStateException("temporary network loss")
            RemoteControlLease(
                leaseId = leaseId,
                expiresAtEpochMillis = 20_000,
                version = leaseVersion + 1,
            )
        }
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            leaseRenewalIntervalMillis = 5_000,
            leaseRenewalRetryIntervalMillis = 1_000,
        )
        session.onConnected()
        session.grantControl("lease", 12_000, leaseVersion = 7)

        advanceTimeBy(5_000)
        runCurrent()
        val afterFailure = assertIs<RemoteSessionState.Controlling>(session.state.value)
        assertEquals(12_000L, afterFailure.expiresAtEpochMillis)
        assertFalse(engine.readOnlyState)

        advanceTimeBy(1_000)
        runCurrent()
        val renewed = assertIs<RemoteSessionState.Controlling>(session.state.value)
        assertEquals(20_000L, renewed.expiresAtEpochMillis)
        assertEquals(8L, renewed.leaseVersion)
        assertEquals(listOf("lease" to 7L, "lease" to 7L), outbound.renewals)

        advanceTimeBy(1_000)
        runCurrent()
        assertIs<RemoteSessionState.Controlling>(session.state.value)
        session.close()
    }

    @Test
    fun authoritativeRenewalRejectionMovesControlToReadOnlyImmediately() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound().apply {
            onRenewLease = { _, _ -> throw RemoteControlLeaseLostException("LEASE_NOT_HELD") }
        }
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
            leaseRenewalIntervalMillis = 1_000,
        )
        session.onConnected()
        session.grantControl("lease", 10_000, leaseVersion = 7)

        advanceTimeBy(1_000)
        runCurrent()

        assertIs<RemoteSessionState.ReadOnly>(session.state.value)
        assertTrue(engine.readOnlyState)
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
    fun unknownInputDeliveryKeepsHealthyControlLease() = runTest {
        val inputId = UUID.fromString("00000000-0000-0000-0000-000000000013")
        val outbound = RecordingOutbound(
            onInput = { throw RemoteInputDeliveryUnknownException("confirmation timed out") },
        )
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
            InputSubmission.DeliveryUnknown(inputId),
            session.submitInput("possibly-delivered".encodeToByteArray()),
        )
        assertEquals(1, outbound.inputAttempts)
        assertIs<RemoteSessionState.Controlling>(session.state.value)
        session.close()
    }

    @Test
    fun delayedInputConfirmationDoesNotBlockLaterEngineInput() = runTest {
        val firstStarted = CompletableDeferred<Unit>()
        val releaseFirst = CompletableDeferred<Unit>()
        val secondStarted = CompletableDeferred<Unit>()
        val engine = RecordingEngine()
        val outbound = RecordingOutbound(
            onInput = { input ->
                if (input.bytes.contentEquals(byteArrayOf(1))) {
                    firstStarted.complete(Unit)
                    releaseFirst.await()
                } else if (input.bytes.contentEquals(byteArrayOf(2))) {
                    secondStarted.complete(Unit)
                }
            },
        )
        val session = RemoteTerminalSession(
            engine,
            outbound,
            backgroundScope,
            clockMillis = { testScheduler.currentTime },
        )
        session.start()
        session.onConnected()
        session.grantControl("lease", 10_000)
        runCurrent()

        engine.emitInput(byteArrayOf(1))
        runCurrent()
        firstStarted.await()
        engine.emitInput(byteArrayOf(2))
        runCurrent()

        assertTrue(secondStarted.isCompleted)
        releaseFirst.complete(Unit)
        runCurrent()
        session.close()
    }

    @Test
    fun authoritativeInputRejectionDoesNotBreakAHealthyControlLease() = runTest {
        val inputId = UUID.fromString("00000000-0000-0000-0000-000000000004")
        val outbound = object : RemoteSessionOutbound {
            override suspend fun resume(lastAckedSequence: Long) = Unit
            override suspend fun persistAcknowledgement(expectedRendererGeneration: Long, highestContiguousSequence: Long) =
                DurableAckCommit(highestContiguousSequence, expectedRendererGeneration)
            override suspend fun resetAcknowledgement(expectedRendererGeneration: Long) =
                DurableAckCommit(0, expectedRendererGeneration + 1)
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
            override suspend fun persistAcknowledgement(expectedRendererGeneration: Long, highestContiguousSequence: Long) =
                DurableAckCommit(highestContiguousSequence, expectedRendererGeneration)
            override suspend fun resetAcknowledgement(expectedRendererGeneration: Long) =
                DurableAckCommit(0, expectedRendererGeneration + 1)
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
    fun terminalSizeEventsArePresentationOnlyAndNeverReachOutbound() = runTest {
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
        advanceTimeBy(150)
        runCurrent()
        assertTrue(outbound.sizes.isEmpty())

        engine.emitSize(TerminalSize(100, 30))
        advanceTimeBy(200)
        runCurrent()
        assertTrue(outbound.sizes.isEmpty())
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

    @Test
    fun orderedObserverRunsAfterRendererAndOwnsOutputBytes() = runTest {
        val engine = RecordingEngine()
        val observed = CompletableDeferred<Pair<Long, ByteArray>>()
        val observer = object : RemoteSessionOrderedEventObserver {
            override suspend fun observe(notification: OrderedRemoteSessionNotification) {
                if (notification is OrderedRemoteSessionNotification.Applied) {
                    assertEquals(1, engine.feeds.size)
                    val output = assertIs<RemoteSessionEvent.Output>(notification.event)
                    observed.complete(notification.rendererGeneration to output.bytes.copyOf())
                }
            }
        }
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = RecordingOutbound(),
            parentScope = backgroundScope,
            orderedEventObserver = observer,
        )
        val bytes = byteArrayOf(7, 8)

        assertEquals(EventAcceptance.Applied(1), session.accept(RemoteSessionEvent.Output(1, bytes)))
        bytes.fill(0)
        val notification = observed.awaitObserverCompletion()
        assertArrayEquals(byteArrayOf(7, 8), notification.second)
        assertEquals(1L, notification.first)
        session.close()
    }

    @Test
    fun orderedObserverFailureCannotChangeRendererOrAcknowledgements() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val observerClosed = CompletableDeferred<Unit>()
        val observer = object : RemoteSessionOrderedEventObserver {
            override suspend fun observe(notification: OrderedRemoteSessionNotification) {
                throw AssertionError("diagnostic shadow failed")
            }

            override fun close() {
                observerClosed.complete(Unit)
            }
        }
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = outbound,
            parentScope = backgroundScope,
            orderedEventObserver = observer,
        )

        assertEquals(EventAcceptance.Applied(1), session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1))))
        observerClosed.awaitObserverCompletion()
        assertEquals(EventAcceptance.Applied(2), session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2))))
        runCurrent()

        assertEquals(listOf(1L, 2L), engine.feeds.map(Pair<Long, ByteArray>::first))
        assertEquals(2L, session.state.value.lastAckedSequence)
        assertEquals(2L, outbound.persistedAcknowledgements.last())
        assertEquals(
            outbound.persistedAcknowledgements.sorted().distinct(),
            outbound.persistedAcknowledgements,
        )
        session.close()
    }

    @Test
    fun orderedObserverOverflowRetiresOnlyObserver() = runTest {
        val engine = RecordingEngine()
        val outbound = RecordingOutbound()
        val observingFirst = CompletableDeferred<Unit>()
        val observerClosed = CompletableDeferred<Unit>()
        val observer = object : RemoteSessionOrderedEventObserver {
            override suspend fun observe(notification: OrderedRemoteSessionNotification) {
                observingFirst.complete(Unit)
                awaitCancellation()
            }

            override fun close() {
                observerClosed.complete(Unit)
            }
        }
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = outbound,
            parentScope = backgroundScope,
            orderedEventObserver = observer,
            orderedEventObserverCapacity = 1,
        )

        session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1)))
        observingFirst.awaitObserverCompletion()
        session.accept(RemoteSessionEvent.Output(2, byteArrayOf(2)))
        session.accept(RemoteSessionEvent.Output(3, byteArrayOf(3)))
        observerClosed.awaitObserverCompletion()
        assertEquals(EventAcceptance.Applied(4), session.accept(RemoteSessionEvent.Output(4, byteArrayOf(4))))
        runCurrent()

        assertEquals(listOf(1L, 2L, 3L, 4L), engine.feeds.map(Pair<Long, ByteArray>::first))
        assertEquals(4L, session.state.value.lastAckedSequence)
        session.close()
    }

    @Test
    fun rendererResetStartsANewObservedGenerationBeforeReplay() = runTest {
        val notifications = mutableListOf<OrderedRemoteSessionNotification>()
        val replayObserved = CompletableDeferred<Unit>()
        val observer = object : RemoteSessionOrderedEventObserver {
            override suspend fun observe(notification: OrderedRemoteSessionNotification) {
                synchronized(notifications) { notifications += notification }
                if (notification is OrderedRemoteSessionNotification.Applied && notification.event.sequence == 2L) {
                    replayObserved.complete(Unit)
                }
            }
        }
        val session = RemoteTerminalSession(
            engine = RecordingEngine(),
            outbound = RecordingOutbound(),
            parentScope = backgroundScope,
            orderedEventObserver = observer,
        )

        assertEquals(
            2L,
            session.resetAndReplay(
                listOf(
                    RemoteSessionEvent.Title(1, "shell"),
                    RemoteSessionEvent.Output(2, byteArrayOf(2)),
                ),
            ),
        )
        replayObserved.awaitObserverCompletion()
        val copy = synchronized(notifications) { notifications.toList() }

        assertEquals(3, copy.size)
        assertIs<OrderedRemoteSessionNotification.RendererReset>(copy[0])
        assertEquals(listOf(2L, 2L, 2L), copy.map(OrderedRemoteSessionNotification::rendererGeneration))
        assertEquals(listOf(1L, 2L), copy.drop(1).map { (it as OrderedRemoteSessionNotification.Applied).event.sequence })
        session.close()
    }

    @Test
    fun duplicateIsNotObservedAndHistoryGapInvalidatesWithoutAdvancingAck() = runTest {
        val notifications = java.util.concurrent.CopyOnWriteArrayList<OrderedRemoteSessionNotification>()
        val invalidated = CompletableDeferred<Unit>()
        val observer = object : RemoteSessionOrderedEventObserver {
            override suspend fun observe(notification: OrderedRemoteSessionNotification) {
                notifications += notification
                if (notification is OrderedRemoteSessionNotification.RendererInvalidated) {
                    invalidated.complete(Unit)
                }
            }
        }
        val outbound = RecordingOutbound()
        val session = RemoteTerminalSession(
            engine = RecordingEngine(),
            outbound = outbound,
            parentScope = backgroundScope,
            orderedEventObserver = observer,
        )

        assertEquals(EventAcceptance.Applied(1), session.accept(RemoteSessionEvent.Output(1, byteArrayOf(1))))
        assertEquals(EventAcceptance.Duplicate(1), session.accept(RemoteSessionEvent.Output(1, byteArrayOf(9))))
        assertEquals(EventAcceptance.HistoryGap(2, 3), session.accept(RemoteSessionEvent.Output(3, byteArrayOf(3))))
        invalidated.awaitObserverCompletion()
        runCurrent()

        assertEquals(2, notifications.size)
        assertIs<OrderedRemoteSessionNotification.Applied>(notifications[0])
        assertIs<OrderedRemoteSessionNotification.RendererInvalidated>(notifications[1])
        assertEquals(listOf(1L), outbound.persistedAcknowledgements)
        session.close()
    }

    @Test
    fun orderedObserverOutputBudgetOverflowCannotStopProductionRenderer() = runTest {
        val observingFirst = CompletableDeferred<Unit>()
        val observerClosed = CompletableDeferred<Unit>()
        val observer = object : RemoteSessionOrderedEventObserver {
            override suspend fun observe(notification: OrderedRemoteSessionNotification) {
                observingFirst.complete(Unit)
                awaitCancellation()
            }

            override fun close() {
                observerClosed.complete(Unit)
            }
        }
        val engine = RecordingEngine()
        val session = RemoteTerminalSession(
            engine = engine,
            outbound = RecordingOutbound(),
            parentScope = backgroundScope,
            orderedEventObserver = observer,
            orderedEventObserverCapacity = 8,
        )

        session.accept(RemoteSessionEvent.Output(1, ByteArray(1024 * 1024)))
        observingFirst.awaitObserverCompletion()
        for (sequence in 2L..5L) {
            session.accept(RemoteSessionEvent.Output(sequence, ByteArray(1024 * 1024)))
        }
        observerClosed.awaitObserverCompletion()
        assertEquals(EventAcceptance.Applied(6), session.accept(RemoteSessionEvent.Output(6, byteArrayOf(6))))
        runCurrent()

        assertEquals((1L..6L).toList(), engine.feeds.map { it.first })
        assertEquals(6L, session.state.value.lastAckedSequence)
        session.close()
    }

    private suspend fun <T> CompletableDeferred<T>.awaitObserverCompletion(): T =
        withContext(Dispatchers.Default.limitedParallelism(1)) {
            withTimeout(5_000) { await() }
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
        private val onInput: suspend (RemoteInput) -> Unit = {},
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
        var resetCount = 0
        var onRenewLease: suspend (String, Long) -> RemoteControlLease? = { _, _ -> null }

        override suspend fun resume(lastAckedSequence: Long) {
            resumes += lastAckedSequence
        }

        override suspend fun persistAcknowledgement(
            expectedRendererGeneration: Long,
            highestContiguousSequence: Long,
        ): DurableAckCommit {
            if (failPersist) throw IllegalStateException("ACK persistence offline")
            onPersist(highestContiguousSequence)
            persistedAcknowledgements += highestContiguousSequence
            return DurableAckCommit(highestContiguousSequence, expectedRendererGeneration)
        }

        override suspend fun resetAcknowledgement(expectedRendererGeneration: Long): DurableAckCommit {
            resetCount++
            persistedAcknowledgements.clear()
            return DurableAckCommit(0, expectedRendererGeneration + 1)
        }

        override suspend fun acknowledge(highestContiguousSequence: Long) {
            if (failAck) throw IllegalStateException("ACK offline")
            acknowledgements += highestContiguousSequence
            log?.add("ack:$highestContiguousSequence")
        }

        override suspend fun sendInput(input: RemoteInput) {
            inputAttempts++
            if (failInput) throw IllegalStateException("offline")
            onInput(input)
            inputs += input.copy(bytes = input.bytes.copyOf())
        }

        override suspend fun sendResize(size: TerminalSize) {
            sizes += size
        }

        override suspend fun renewLease(leaseId: String, leaseVersion: Long): RemoteControlLease? {
            renewals += leaseId to leaseVersion
            return onRenewLease(leaseId, leaseVersion)
        }
    }
}

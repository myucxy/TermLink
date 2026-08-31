package dev.termlink.product

import java.util.concurrent.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalSnapshotBootstrapTest {
    @Test
    fun snapshotRuntimeRequiresBothLocalOptInAndPcCapability() {
        val capability = dev.termlink.model.PcCapabilityIds.TERMINAL_SNAPSHOT_V2
        assertFalse(terminalSnapshotRuntimeNegotiated(false, setOf(capability)))
        assertFalse(terminalSnapshotRuntimeNegotiated(true, emptySet()))
        assertTrue(terminalSnapshotRuntimeNegotiated(true, setOf(capability)))
    }

    @Test
    fun missingV2CapabilitySkipsRemoteSnapshotReadinessWait() = runBlocking {
        val events = mutableListOf<String>()
        val result = restoreReplaceableBoundOrRemoteCheckpoint(
            negotiated = false,
            restoreBound = { events += "bound"; "unexpected" },
            abandonRenderer = { events += "abandon" },
            restoreRemote = { events += "remote"; "unexpected" },
        )

        assertNull(result)
        assertEquals(emptyList<String>(), events)
    }

    @Test
    fun bcf7BoundSnapshotGapResetsRendererAndRestoresTheRemoteDurableAck() = runBlocking {
        val events = mutableListOf<String>()
        var rendererGeneration = 2L
        var rendererSnapshotSequence = 7uL
        var durableAcknowledgement = 1629uL

        val restored = restoreReplaceableBoundOrRemoteCheckpoint(
            negotiated = true,
            restoreBound = {
                events += "bound:$rendererSnapshotSequence:$durableAcknowledgement:$rendererGeneration"
                throw sessionOpenException(SessionOpenFailureCode.HISTORY_GAP)
            },
            abandonRenderer = {
                rendererSnapshotSequence = 0uL
                events += "abandon"
            },
            restoreRemote = {
                val remoteSnapshotSequence = 1629uL
                check(remoteSnapshotSequence >= durableAcknowledgement)
                rendererSnapshotSequence = remoteSnapshotSequence
                durableAcknowledgement = maxOf(durableAcknowledgement, remoteSnapshotSequence)
                rendererGeneration += 1L
                events += "remote:$remoteSnapshotSequence:$rendererGeneration"
                "controlling"
            },
        )

        assertEquals("controlling", restored)
        assertEquals(1629uL, rendererSnapshotSequence)
        assertEquals(1629uL, durableAcknowledgement)
        assertEquals(3L, rendererGeneration)
        assertEquals(
            listOf("bound:7:1629:2", "abandon", "remote:1629:3"),
            events,
        )
    }

    @Test
    fun boundSnapshotIntegrityFailureRemainsFailClosed() {
        var abandoned = false

        val failure = assertThrows(SessionOpenException::class.java) {
            runBlocking {
                restoreReplaceableBoundCheckpoint<String>(
                    restoreBound = {
                        throw sessionOpenException(SessionOpenFailureCode.SNAPSHOT_INTEGRITY)
                    },
                    abandonRenderer = { abandoned = true },
                )
            }
        }

        assertEquals(SessionOpenFailureCode.SNAPSHOT_INTEGRITY, failure.failure.code)
        assertFalse(abandoned)
    }

    @Test
    fun productionReadinessPolicyCoversSlowWindowsRegistrationWithShortRequests() {
        assertEquals(30_000L, TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS)
        assertEquals(1_000L, TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS)
        assertEquals(128, TERMINAL_SNAPSHOT_MAX_LIVE_SUFFIX_EVENTS)
        assertEquals(
            listOf(
                100L, 200L, 400L, 800L, 1_500L,
                2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L,
                2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L, 2_000L,
            ),
            TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS,
        )
        assertTrue(
            TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS <
                TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
        )
        assertTrue(
            TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS.dropLast(1).sum() <
                TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
        )
        assertTrue(
            TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS.sum() >=
                TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
        )
    }

    @Test
    fun freshSessionCanUseOnlyTheBoundedAgentReplayWindow() {
        assertTrue(canFreshSessionReplayFromZero(0uL, 0uL))
        assertTrue(canFreshSessionReplayFromZero(128uL, 0uL))
        assertFalse(canFreshSessionReplayFromZero(129uL, 0uL))
        assertFalse(canFreshSessionReplayFromZero(1uL, 1uL))
    }

    @Test
    fun runtimeDisabledDoesNotInvokeSnapshotOperations() = runBlocking {
        val events = mutableListOf<String>()

        val restored = restoreSnapshotWhenEnabled(
            enabled = false,
            restoreBound = { events += "bound"; "checkpoint" },
            latestRemote = { events += "latest"; "remote" },
            restoreRemote = { events += "remote"; "restored" },
        )

        assertNull(restored)
        assertEquals(emptyList<String>(), events)
    }

    @Test
    fun sequenceZeroFallbackIsAllowedOnlyForAnActuallyEmptySession() {
        assertFalse(canSafelySubscribeFromZeroWithoutSnapshot(0uL, 0uL, false))
        assertTrue(canSafelySubscribeFromZeroWithoutSnapshot(0uL, 0uL, true))
        assertFalse(canSafelySubscribeFromZeroWithoutSnapshot(1uL, 0uL, true))
        assertFalse(canSafelySubscribeFromZeroWithoutSnapshot(0uL, 1uL, true))
        assertFalse(canSafelySubscribeFromZeroWithoutSnapshot(1uL, 1uL, true))
    }

    @Test
    fun unsupportedSnapshotSettlesSnapshotPollingSoChunkPollingCanUseTheRemainingWindow() {
        assertTrue(snapshotReadinessIsSettled(snapshotAvailable = true, failure = null))
        assertTrue(
            snapshotReadinessIsSettled(
                snapshotAvailable = false,
                failure = SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED),
            ),
        )
        assertFalse(
            snapshotReadinessIsSettled(
                snapshotAvailable = false,
                failure = SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_PRODUCTION_FAILED),
            ),
        )
        assertFalse(snapshotReadinessIsSettled(snapshotAvailable = false, failure = null))
    }

    @Test
    fun boundedReplayRequiresAnExactPrefixAndDistinguishesPendingGapAndLimit() {
        assertEquals(
            BoundedEventReplayReadiness.READY,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(37uL, 269uL), HistorySequenceRange(1uL, 36uL)),
                throughSequence = 268uL,
            ),
        )
        assertEquals(
            BoundedEventReplayReadiness.NOT_YET_COMPLETE,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(1uL, 32uL), HistorySequenceRange(33uL, 39uL)),
                throughSequence = 2_716uL,
            ),
        )
        assertEquals(
            BoundedEventReplayReadiness.GAP,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(2uL, 40uL)),
                throughSequence = 40uL,
            ),
        )
        assertEquals(
            BoundedEventReplayReadiness.GAP,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(1uL, 20uL), HistorySequenceRange(20uL, 40uL)),
                throughSequence = 40uL,
            ),
        )
        assertEquals(
            BoundedEventReplayReadiness.GAP,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(1uL, 40uL), HistorySequenceRange(20uL, 40uL)),
                throughSequence = 40uL,
            ),
        )
        assertEquals(
            BoundedEventReplayReadiness.READY,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(1uL, 40uL), HistorySequenceRange(41uL, 50uL)),
                throughSequence = 40uL,
            ),
        )
        assertEquals(
            BoundedEventReplayReadiness.LIMIT_EXCEEDED,
            boundedEventReplayReadiness(
                listOf(HistorySequenceRange(1uL, 4_097uL)),
                throughSequence = 4_097uL,
            ),
        )
    }

    @Test
    fun tailReplayStartsAfterOneAndEndsAtTheLatestAuthoritativeTail() {
        val selection = boundedTailReplaySelection(
            candidates = listOf(
                HistorySequenceRange(9_801uL, 9_900uL),
                HistorySequenceRange(9_951uL, 10_000uL),
                HistorySequenceRange(9_901uL, 9_950uL),
            ),
            authoritativeTail = 10_000uL,
            sequenceRange = { it },
            maximumEvents = 100,
        )

        assertEquals(BoundedEventReplayReadiness.READY, selection.readiness)
        assertEquals(9_901uL, selection.start)
        assertEquals(10_000uL, selection.target)
        assertEquals(100, selection.eventCount)
        assertEquals(
            listOf(
                HistorySequenceRange(9_901uL, 9_950uL),
                HistorySequenceRange(9_951uL, 10_000uL),
            ),
            selection.candidates,
        )
    }

    @Test
    fun tailReplayRejectsAGapInsideTheSelectedRecentWindow() {
        val selection = boundedTailReplaySelection(
            candidates = listOf(
                HistorySequenceRange(101uL, 150uL),
                HistorySequenceRange(152uL, 200uL),
            ),
            authoritativeTail = 200uL,
            sequenceRange = { it },
            maximumEvents = 100,
        )

        assertEquals(BoundedEventReplayReadiness.GAP, selection.readiness)
        assertTrue(selection.candidates.isEmpty())
        assertEquals(
            BoundedEventReplayReadiness.GAP,
            boundedTailReplaySelection(
                candidates = listOf(
                    HistorySequenceRange(101uL, 200uL),
                    HistorySequenceRange(101uL, 200uL),
                ),
                authoritativeTail = 200uL,
                sequenceRange = { it },
                maximumEvents = 100,
            ).readiness,
        )
    }

    @Test
    fun tailReplayTruncatesWholeOlderChunksInsteadOfExceedingItsLimit() {
        val ranges = (0 until 9).map { index ->
            val start = index.toULong() * 512uL + 1uL
            HistorySequenceRange(start, start + 511uL)
        }
        val selection = boundedTailReplaySelection(
            candidates = ranges,
            authoritativeTail = 4_608uL,
            sequenceRange = { it },
        )

        assertEquals(BoundedEventReplayReadiness.READY, selection.readiness)
        assertEquals(513uL, selection.start)
        assertEquals(4_608uL, selection.target)
        assertEquals(4_096, selection.eventCount)
        assertEquals(ranges.drop(1), selection.candidates)
        assertEquals(
            BoundedEventReplayReadiness.LIMIT_EXCEEDED,
            boundedTailReplaySelection(
                candidates = listOf(HistorySequenceRange(1uL, 4_097uL)),
                authoritativeTail = 4_097uL,
                sequenceRange = { it },
            ).readiness,
        )
    }

    @Test
    fun tailCatalogWaitsForPublicationAndTracksTailAdvanceDuringOpen() = runBlocking {
        var attempts = 0
        val result = awaitBoundedTailReplayCatalog(
            initialTarget = 200uL,
            latestObservation = {
                attempts += 1
                when (attempts) {
                    1 -> BoundedTailReplayCatalogObservation(
                        authoritativeTail = 200uL,
                        candidates = listOf(HistorySequenceRange(101uL, 180uL)),
                    )
                    2 -> BoundedTailReplayCatalogObservation(
                        authoritativeTail = 220uL,
                        candidates = listOf(HistorySequenceRange(101uL, 200uL)),
                    )
                    else -> BoundedTailReplayCatalogObservation(
                        authoritativeTail = 220uL,
                        candidates = listOf(
                            HistorySequenceRange(101uL, 180uL),
                            HistorySequenceRange(181uL, 220uL),
                        ),
                    )
                }
            },
            sequenceRange = { it },
            retryDelaysMillis = listOf(100L, 200L),
            timeoutMillis = 1_000L,
            maximumEvents = 200,
            pause = { },
        )

        assertEquals(3, attempts)
        assertEquals(BoundedEventReplayReadiness.READY, result.readiness)
        assertEquals(101uL, result.start)
        assertEquals(220uL, result.target)
        assertEquals(120, result.eventCount)
    }

    @Test
    fun tailCatalogPropagatesCallerCancellation() {
        val cancellation = CancellationException("tail catalog cancelled")

        val thrown = assertThrows(CancellationException::class.java) {
            runBlocking {
                awaitBoundedTailReplayCatalog<HistorySequenceRange>(
                    initialTarget = 200uL,
                    latestObservation = { throw cancellation },
                    sequenceRange = { it },
                    retryDelaysMillis = listOf(100L),
                    timeoutMillis = 1_000L,
                    pause = { error("pause must not run") },
                )
            }
        }

        assertEquals("tail catalog cancelled", thrown.message)
    }

    @Test
    fun tailReplayRereadsAuthorityAndAllowsOnlyABoundedRealtimeSuffix() = runBlocking {
        var tailReads = 0
        assertEquals(
            FullPrefixRealtimeSuffixValidation.READY,
            validateTailReplayRealtimeSuffix(1_000uL) {
                tailReads += 1
                5_096uL
            },
        )
        assertEquals(1, tailReads)
        assertEquals(
            FullPrefixRealtimeSuffixValidation.OUTSIDE_WINDOW,
            validateTailReplayRealtimeSuffix(1_000uL) { 5_097uL },
        )
    }

    @Test
    fun chunkCatalogPublishedLaterSucceedsAgainstItsLatestObservedWatermark() = runBlocking {
        var attempts = 0
        val result = awaitBoundedEventReplayCatalog(
            initialTarget = 40uL,
            initialCandidates = emptyList<HistorySequenceRange>(),
            sequenceRange = { it },
            latestObservation = {
                attempts += 1
                when (attempts) {
                    1 -> BoundedEventReplayCatalogObservation(
                        authoritativeTail = 40uL,
                        candidates = listOf(HistorySequenceRange(1uL, 32uL)),
                    )
                    2 -> BoundedEventReplayCatalogObservation(
                        authoritativeTail = 48uL,
                        candidates = listOf(HistorySequenceRange(1uL, 40uL)),
                    )
                    else -> BoundedEventReplayCatalogObservation(
                        authoritativeTail = 48uL,
                        candidates = listOf(HistorySequenceRange(1uL, 48uL)),
                    )
                }
            },
            retryDelaysMillis = listOf(100L, 200L),
            timeoutMillis = 1_000L,
            pause = { },
        )

        assertEquals(3, attempts)
        assertEquals(48uL, result.target)
        assertEquals(BoundedEventReplayReadiness.READY, result.readiness)
        assertEquals(listOf(HistorySequenceRange(1uL, 48uL)), result.candidates)
    }

    @Test
    fun chunkCatalogPollingStopsAtTheBoundedTimeout() = runBlocking {
        var attempts = 0
        val result = awaitBoundedEventReplayCatalog(
            initialTarget = 40uL,
            initialCandidates = emptyList<HistorySequenceRange>(),
            sequenceRange = { it },
            latestObservation = {
                attempts += 1
                BoundedEventReplayCatalogObservation(
                    authoritativeTail = 40uL,
                    candidates = emptyList(),
                )
            },
            retryDelaysMillis = listOf(10_000L),
            timeoutMillis = 20L,
            pause = { delay(it) },
        )

        assertEquals(1, attempts)
        assertEquals(40uL, result.target)
        assertEquals(BoundedEventReplayReadiness.NOT_YET_COMPLETE, result.readiness)
    }

    @Test
    fun chunkCatalogPollingPropagatesCallerCancellation() {
        val cancellation = CancellationException("catalog polling cancelled")

        val thrown = assertThrows(CancellationException::class.java) {
            runBlocking {
                awaitBoundedEventReplayCatalog(
                    initialTarget = 40uL,
                    initialCandidates = emptyList<HistorySequenceRange>(),
                    sequenceRange = { it },
                    latestObservation = { throw cancellation },
                    retryDelaysMillis = listOf(100L),
                    timeoutMillis = 1_000L,
                    pause = { error("pause must not run") },
                )
            }
        }

        assertEquals("catalog polling cancelled", thrown.message)
    }

    @Test
    fun fullPrefixReplayRereadsTailAndRejectsARealtimeWindowGapBeforeAck() = runBlocking {
        val restoredTarget = 100uL
        var authoritativeTail = restoredTarget
        var tailReads = 0

        // Simulates output produced while the complete prefix is being
        // downloaded and applied. Validation must read this newer value.
        authoritativeTail = 229uL
        val outsideWindow = validateFullPrefixRealtimeSuffix(restoredTarget) {
            tailReads += 1
            authoritativeTail
        }

        assertEquals(1, tailReads)
        assertEquals(FullPrefixRealtimeSuffixValidation.OUTSIDE_WINDOW, outsideWindow)
        authoritativeTail = 228uL
        assertEquals(
            FullPrefixRealtimeSuffixValidation.READY,
            validateFullPrefixRealtimeSuffix(restoredTarget) { authoritativeTail },
        )
        assertTrue(fullPrefixHasBoundedRealtimeSuffix(restoredTarget, 228uL))
        assertFalse(fullPrefixHasBoundedRealtimeSuffix(restoredTarget, 229uL))
    }

    @Test
    fun staleZeroCatalogCannotReplayFromZeroAfterReadinessObservesOutput() = runBlocking {
        data class Observation(val tail: ULong, val snapshotReady: Boolean)

        var latestAuthoritativeTail = 0uL
        var authoritativeReadinessObserved = false
        val restored = restoreSnapshotWhenEnabled<Observation, String>(
            enabled = true,
            restoreBound = { null },
            latestRemote = { Observation(tail = 1_000uL, snapshotReady = false) },
            remoteReady = Observation::snapshotReady,
            onRemoteObserved = { observation ->
                authoritativeReadinessObserved = true
                latestAuthoritativeTail = maxOf(latestAuthoritativeTail, observation.tail)
            },
            restoreRemote = { error("an unavailable snapshot must not be restored") },
            remoteRetryDelaysMillis = emptyList(),
        )

        assertNull(restored)
        assertEquals(1_000uL, latestAuthoritativeTail)
        assertTrue(authoritativeReadinessObserved)
        assertFalse(
            canSafelySubscribeFromZeroWithoutSnapshot(
                latestAuthoritativeTail,
                durableAcknowledgement = 0uL,
                authoritativeReadinessObserved = authoritativeReadinessObserved,
            ),
        )
    }

    @Test
    fun persistentCheckpointWinsWithoutRemoteRequest() = runBlocking {
        val events = mutableListOf<String>()

        val restored = restoreSnapshotWhenEnabled(
            enabled = true,
            restoreBound = { events += "bound"; "checkpoint" },
            latestRemote = { events += "latest"; "remote" },
            restoreRemote = { events += "remote"; "restored" },
        )

        assertEquals("checkpoint", restored)
        assertEquals(listOf("bound"), events)
    }

    @Test
    fun outdatedSnapshotWaitsForANearTailSnapshotWithBoundedSuffix() = runBlocking {
        data class RestoredAnchor(val snapshotSequence: ULong, val transcriptBeforeLineId: ULong)
        data class Observation(
            val currentTail: ULong,
            val snapshotSequence: ULong,
            val transcriptBeforeLineId: ULong,
        )

        var remoteAttempts = 0
        val pauses = mutableListOf<Long>()
        val restored = restoreSnapshotWhenEnabled<Observation, RestoredAnchor>(
            enabled = true,
            restoreBound = { null },
            latestRemote = {
                remoteAttempts += 1
                if (remoteAttempts == 1) {
                    Observation(
                        currentTail = 1_000uL,
                        snapshotSequence = 700uL,
                        transcriptBeforeLineId = 747uL,
                    )
                } else {
                    Observation(
                        currentTail = 1_000uL,
                        snapshotSequence = 900uL,
                        transcriptBeforeLineId = 947uL,
                    )
                }
            },
            remoteReady = { observation ->
                snapshotHasBoundedLiveSuffix(
                    observation.snapshotSequence,
                    observation.currentTail,
                )
            },
            restoreRemote = { observation ->
                RestoredAnchor(observation.snapshotSequence, observation.transcriptBeforeLineId)
            },
            remoteRetryDelaysMillis = listOf(100L, 200L, 400L),
            pause = { pauses += it },
        )

        assertEquals(RestoredAnchor(snapshotSequence = 900uL, transcriptBeforeLineId = 947uL), restored)
        assertEquals(2, remoteAttempts)
        assertEquals(listOf(100L), pauses)
        assertFalse(snapshotHasBoundedLiveSuffix(700uL, currentSessionSequence = 1_000uL))
        assertTrue(snapshotHasBoundedLiveSuffix(900uL, currentSessionSequence = 1_000uL))
        assertTrue(snapshotHasBoundedLiveSuffix(1_000uL, currentSessionSequence = 1_000uL))
        assertFalse(snapshotHasBoundedLiveSuffix(1_001uL, currentSessionSequence = 1_000uL))
    }

    @Test
    fun authoritativeTailDriftAfterPreparePreventsBindInstallAndSocketCreation() = runBlocking {
        val events = mutableListOf<String>()
        var authoritativeTail = 900uL
        var socketStarted = false

        val restored = prepareBindInstallSnapshot(
            loadAndCache = { events += "cache"; 900uL },
            prepare = { sequence ->
                events += "prepare:$sequence"
                authoritativeTail = 1_100uL
                sequence
            },
            preparedIsCurrent = { sequence ->
                events += "confirm:$sequence:$authoritativeTail"
                snapshotHasBoundedLiveSuffix(sequence, authoritativeTail)
            },
            bind = { events += "bind"; "bound" },
            installAndMap = { _, _ -> events += "install"; "exact" },
        )
        if (restored != null) socketStarted = true

        assertNull(restored)
        assertFalse(socketStarted)
        assertEquals(
            listOf("cache", "prepare:900", "confirm:900:1100"),
            events,
        )
    }

    @Test
    fun transientMetadataFailureUsesTheRemainingReadinessWindow() = runBlocking {
        var attempts = 0
        val restored = restoreSnapshotWhenEnabled(
            enabled = true,
            restoreBound = { null },
            latestRemote = {
                attempts += 1
                if (attempts == 1) throw java.io.IOException("transient")
                "ready"
            },
            restoreRemote = { it },
            remoteRetryDelaysMillis = listOf(0L),
            pause = { },
        )

        assertEquals("ready", restored)
        assertEquals(2, attempts)
    }

    @Test
    fun snapshotPublishedAfterOldThreeSecondBudgetStillRestores() = runBlocking {
        var virtualElapsedMillis = 0L
        var remoteAttempts = 0

        val restored = restoreSnapshotWhenEnabled(
            enabled = true,
            restoreBound = { null },
            latestRemote = {
                remoteAttempts += 1
                "ready".takeIf { virtualElapsedMillis > 3_000L }
            },
            restoreRemote = { it },
            remoteRetryDelaysMillis = TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS,
            remoteReadinessTimeoutMillis = TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
            pause = { virtualElapsedMillis += it },
        )

        assertEquals("ready", restored)
        assertEquals(5_000L, virtualElapsedMillis)
        assertEquals(7, remoteAttempts)
        assertTrue(virtualElapsedMillis < TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS)
    }

    @Test
    fun absoluteReadinessDeadlineCancelsLongRetryPause() = runBlocking {
        var attempts = 0
        val restored = restoreSnapshotWhenEnabled<String, String>(
            enabled = true,
            restoreBound = { null },
            latestRemote = { attempts += 1; null },
            restoreRemote = { error("restore must not run") },
            remoteRetryDelaysMillis = listOf(10_000L),
            remoteReadinessTimeoutMillis = 20L,
            pause = { delay(it) },
        )

        assertNull(restored)
        assertEquals(1, attempts)
    }

    @Test
    fun unsupportedSnapshotProducerStopsAfterBoundedReadinessWindow() = runBlocking {
        var remoteAttempts = 0
        val pauses = mutableListOf<Long>()

        val restored = restoreSnapshotWhenEnabled<String, String>(
            enabled = true,
            restoreBound = { null },
            latestRemote = { remoteAttempts += 1; null },
            restoreRemote = { error("restore must not run") },
            remoteRetryDelaysMillis = listOf(100L, 200L),
            pause = { pauses += it },
        )

        assertNull(restored)
        assertEquals(3, remoteAttempts)
        assertEquals(listOf(100L, 200L), pauses)
    }

    @Test
    fun callerCancellationPropagatesFromReadinessAttempt() {
        val cancellation = CancellationException("session opening cancelled")
        var remoteAttempts = 0

        val thrown = assertThrows(CancellationException::class.java) {
            runBlocking {
                restoreSnapshotWhenEnabled<String, String>(
                    enabled = true,
                    restoreBound = { null },
                    latestRemote = {
                        remoteAttempts += 1
                        throw cancellation
                    },
                    restoreRemote = { error("restore must not run") },
                    remoteRetryDelaysMillis = TERMINAL_SNAPSHOT_READINESS_RETRY_DELAYS_MILLIS,
                    remoteReadinessTimeoutMillis = TERMINAL_SNAPSHOT_READINESS_TIMEOUT_MILLIS,
                    pause = { error("pause must not run") },
                )
            }
        }

        assertEquals("session opening cancelled", thrown.message)
        assertEquals(1, remoteAttempts)
    }

    @Test
    fun remoteSnapshotOrdersCachePrepareBindInstallAndExactCheckpoint() = runBlocking {
        val events = mutableListOf<String>()

        val restored = prepareBindInstallSnapshot(
            loadAndCache = { events += "cache"; "sealed" },
            prepare = { loaded -> events += "prepare:$loaded"; "prepared" },
            bind = { events += "bind"; "bound" },
            installAndMap = { prepared, bound ->
                events += "install:$prepared:$bound"
                "exact"
            },
        )

        assertEquals("exact", restored)
        assertEquals(listOf("cache", "prepare:sealed", "bind", "install:prepared:bound"), events)
    }

    @Test
    fun loadAndPrepareFailuresRemainDistinguishableWithoutBinding() = runBlocking {
        val failures = mutableListOf<String>()

        val loadResult = prepareBindInstallSnapshot<Unit, Unit, Unit, Unit>(
            loadAndCache = { error("download") },
            prepare = { error("prepare must not run") },
            bind = { error("bind must not run") },
            installAndMap = { _, _ -> error("install must not run") },
            onLoadFailure = { failures += "load:${it.message}" },
            onPrepareFailure = { failures += "prepare:${it.message}" },
        )
        val prepareResult = prepareBindInstallSnapshot<Unit, Unit, Unit, Unit>(
            loadAndCache = { Unit },
            prepare = { error("decode") },
            bind = { error("bind must not run") },
            installAndMap = { _, _ -> error("install must not run") },
            onLoadFailure = { failures += "load:${it.message}" },
            onPrepareFailure = { failures += "prepare:${it.message}" },
        )

        assertNull(loadResult)
        assertNull(prepareResult)
        assertEquals(listOf("load:download", "prepare:decode"), failures)
    }

    @Test
    fun installFailureAfterBindPropagatesAndPreservesDurableBinding() {
        val events = mutableListOf<String>()

        val thrown = assertThrows(IllegalStateException::class.java) {
            runBlocking {
                prepareBindInstallSnapshot(
                    loadAndCache = { events += "cache"; Unit },
                    prepare = { events += "prepare"; Unit },
                    bind = { events += "bind"; 7L },
                    installAndMap = { _, _ -> events += "install"; error("install failed") },
                )
            }
        }

        assertEquals("install failed", thrown.message)
        assertEquals(listOf("cache", "prepare", "bind", "install"), events)
    }

    @Test
    fun bindFailurePropagatesWithoutInstallOrReset() {
        val events = mutableListOf<String>()

        assertThrows(IllegalStateException::class.java) {
            runBlocking {
                prepareBindInstallSnapshot(
                    loadAndCache = { events += "cache"; Unit },
                    prepare = { events += "prepare"; Unit },
                    bind = { events += "bind"; error("bind failed") },
                    installAndMap = { _, _ -> events += "install"; "exact" },
                )
            }
        }

        assertEquals(listOf("cache", "prepare", "bind"), events)
    }
}

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
    fun permanentSnapshotRejectionSettlesReadinessWithoutDelayingChunkFallback() {
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
            resetAfterInstallFailure = { events += "reset"; "empty" },
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
            resetAfterInstallFailure = { error("reset must not run") },
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
            resetAfterInstallFailure = { error("reset must not run") },
            onLoadFailure = { failures += "load:${it.message}" },
            onPrepareFailure = { failures += "prepare:${it.message}" },
        )
        val prepareResult = prepareBindInstallSnapshot<Unit, Unit, Unit, Unit>(
            loadAndCache = { Unit },
            prepare = { error("decode") },
            bind = { error("bind must not run") },
            installAndMap = { _, _ -> error("install must not run") },
            resetAfterInstallFailure = { error("reset must not run") },
            onLoadFailure = { failures += "load:${it.message}" },
            onPrepareFailure = { failures += "prepare:${it.message}" },
        )

        assertNull(loadResult)
        assertNull(prepareResult)
        assertEquals(listOf("load:download", "prepare:decode"), failures)
    }

    @Test
    fun installFailureAfterBindResetsAndReturnsEmpty() = runBlocking {
        val events = mutableListOf<String>()

        val restored = prepareBindInstallSnapshot(
            loadAndCache = { events += "cache"; Unit },
            prepare = { events += "prepare"; Unit },
            bind = { events += "bind"; 7L },
            installAndMap = { _, _ -> events += "install"; error("install failed") },
            resetAfterInstallFailure = { generation -> events += "reset:$generation"; "empty" },
        )

        assertEquals("empty", restored)
        assertEquals(listOf("cache", "prepare", "bind", "install", "reset:7"), events)
    }

    @Test
    fun resetFailurePropagatesBeforeSocketCreation() {
        var socketStarted = false

        assertThrows(IllegalStateException::class.java) {
            runBlocking {
                prepareBindInstallSnapshot(
                    loadAndCache = { Unit },
                    prepare = { Unit },
                    bind = { 8L },
                    installAndMap = { _, _ -> error("install failed") },
                    resetAfterInstallFailure = { error("reset failed") },
                )
                socketStarted = true
            }
        }

        assertFalse(socketStarted)
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
                    resetAfterInstallFailure = { events += "reset"; "empty" },
                )
            }
        }

        assertEquals(listOf("cache", "prepare", "bind"), events)
    }
}

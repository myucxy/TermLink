package dev.termlink.product

import dev.termlink.session.RendererBootstrapState
import java.util.UUID
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
    fun olderHostSnapshotMustReplayToDurableAckBeforeExactCheckpointIsAdvertised() {
        val snapshotSequence = 45uL
        val durableAcknowledgement = 46uL
        val snapshotGeneration = 2L

        assertFalse(
            snapshotSuffixRestoreReady(
                restoredThroughSequence = snapshotSequence,
                durableAcknowledgement = durableAcknowledgement,
                authoritativeTail = durableAcknowledgement,
            ),
        )
        assertTrue(
            snapshotSuffixReplayTargetIsBounded(
                snapshotSequence = snapshotSequence,
                replayTarget = durableAcknowledgement,
            ),
        )

        val restored = RendererBootstrapState.ExactCheckpoint(
            sequence = durableAcknowledgement.toLong(),
            rendererGeneration = snapshotGeneration,
            snapshotId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"),
            ciphertextSha256 = ByteArray(32),
            snapshotSequence = snapshotSequence.toLong(),
        )

        assertEquals(durableAcknowledgement.toLong(), restored.sequence)
        assertEquals(snapshotSequence.toLong(), restored.snapshotSequence)
        assertTrue(
            snapshotSuffixRestoreReady(
                restoredThroughSequence = restored.sequence.toULong(),
                durableAcknowledgement = durableAcknowledgement,
                authoritativeTail = durableAcknowledgement,
            ),
        )
    }

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
        assertEquals(5_000L, TERMINAL_SNAPSHOT_READINESS_REQUEST_TIMEOUT_MILLIS)
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
    fun persistentMetadataTimeoutSurvivesTheAbsoluteReadinessDeadline() {
        data class Observation(
            val tail: ULong,
            val snapshot: String?,
            val failure: SessionOpenFailure?,
        )
        val expected = SessionOpenFailure(SessionOpenFailureCode.SERVICE_TIMEOUT)

        val result = runBlocking {
            awaitTerminalSnapshotReadiness(
                latestObservation = { Observation(697uL, snapshot = null, failure = expected) },
                snapshotOf = Observation::snapshot,
                lastOutputSequenceOf = Observation::tail,
                failureOf = Observation::failure,
                retryDelaysMillis = listOf(10_000L),
                timeoutMillis = 20L,
                pause = { delay(it) },
            )
        }

        assertNull(result.snapshot)
        assertEquals(expected, result.failure)
        val thrown = assertThrows(SessionOpenException::class.java) {
            throwTerminalSnapshotReadinessFailureUnlessUnsupported(result.failure)
        }
        assertEquals(SessionOpenFailureCode.SERVICE_TIMEOUT, thrown.failure.code)
    }

    @Test
    fun laterSnapshotClearsAnEarlierTransientMetadataFailure() = runBlocking {
        data class Observation(
            val tail: ULong,
            val snapshot: String?,
            val failure: SessionOpenFailure?,
        )
        var attempts = 0

        val result = awaitTerminalSnapshotReadiness(
            latestObservation = {
                attempts += 1
                if (attempts == 1) {
                    Observation(
                        tail = 740uL,
                        snapshot = null,
                        failure = SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_METADATA),
                    )
                } else {
                    Observation(tail = 740uL, snapshot = "snapshot-v2", failure = null)
                }
            },
            snapshotOf = Observation::snapshot,
            lastOutputSequenceOf = Observation::tail,
            failureOf = Observation::failure,
            retryDelaysMillis = listOf(0L),
            timeoutMillis = 1_000L,
            pause = { },
        )

        assertEquals("snapshot-v2", result.snapshot)
        assertNull(result.failure)
        assertEquals(2, attempts)
    }

    @Test
    fun recoveredEmptySnapshotObservationClearsAnEarlierFailure() = runBlocking {
        data class Observation(
            val tail: ULong,
            val failure: SessionOpenFailure?,
        )
        var attempts = 0

        val result = awaitTerminalSnapshotReadiness<Observation, String>(
            latestObservation = {
                attempts += 1
                if (attempts == 1) {
                    Observation(
                        740uL,
                        SessionOpenFailure(SessionOpenFailureCode.SERVICE_TIMEOUT),
                    )
                } else {
                    Observation(740uL, failure = null)
                }
            },
            snapshotOf = { null },
            lastOutputSequenceOf = Observation::tail,
            failureOf = Observation::failure,
            retryDelaysMillis = listOf(0L),
            timeoutMillis = 1_000L,
            pause = { },
        )

        assertNull(result.snapshot)
        assertNull(result.failure)
        assertEquals(2, attempts)
    }

    @Test
    fun thrownObservationFailureIsRetriedAndARecoveredSnapshotWins() = runBlocking {
        data class Observation(val snapshot: String?)
        var attempts = 0

        val result = awaitTerminalSnapshotReadiness(
            latestObservation = {
                attempts += 1
                if (attempts == 1) throw java.net.SocketTimeoutException("fixture")
                Observation("snapshot-after-retry")
            },
            snapshotOf = Observation::snapshot,
            lastOutputSequenceOf = { 740uL },
            failureOf = { null },
            retryDelaysMillis = listOf(0L),
            timeoutMillis = 1_000L,
            pause = { },
            observationFailure = {
                SessionOpenFailure(SessionOpenFailureCode.SERVICE_TIMEOUT)
            },
        )

        assertEquals("snapshot-after-retry", result.snapshot)
        assertNull(result.failure)
        assertEquals(2, attempts)
    }

    @Test
    fun snapshotFailureOnlyBlocksChunkFallbackAfterADurableAck() {
        val timeout = SessionOpenFailure(SessionOpenFailureCode.SERVICE_TIMEOUT)
        val unsupported = SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED)

        assertFalse(terminalSnapshotReadinessBlocksChunkFallback(0uL, timeout))
        assertTrue(terminalSnapshotReadinessBlocksChunkFallback(1uL, timeout))
        assertFalse(terminalSnapshotReadinessBlocksChunkFallback(1uL, unsupported))
        assertFalse(terminalSnapshotReadinessBlocksChunkFallback(1uL, failure = null))
    }

    @Test
    fun unsupportedSnapshotStillFallsBackWithoutThrowing() = runBlocking {
        data class Observation(val tail: ULong, val failure: SessionOpenFailure?)
        var attempts = 0
        val unsupported = SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_UNSUPPORTED)

        val result = awaitTerminalSnapshotReadiness<Observation, String>(
            latestObservation = {
                attempts += 1
                Observation(tail = 46uL, failure = unsupported)
            },
            snapshotOf = { null },
            lastOutputSequenceOf = Observation::tail,
            failureOf = Observation::failure,
            retryDelaysMillis = listOf(100L, 200L),
            timeoutMillis = 1_000L,
            pause = { error("unsupported readiness must not pause") },
        )

        assertNull(result.snapshot)
        assertEquals(unsupported, result.failure)
        assertEquals(1, attempts)
        throwTerminalSnapshotReadinessFailureUnlessUnsupported(result.failure)
    }

    @Test
    fun emptySessionSettlesReadinessWithoutReusingAnEarlierFailure() = runBlocking {
        data class Observation(
            val tail: ULong,
            val failure: SessionOpenFailure?,
        )
        var attempts = 0

        val result = awaitTerminalSnapshotReadiness<Observation, String>(
            latestObservation = {
                attempts += 1
                if (attempts == 1) {
                    Observation(52uL, SessionOpenFailure(SessionOpenFailureCode.SNAPSHOT_STALE))
                } else {
                    Observation(0uL, failure = null)
                }
            },
            snapshotOf = { null },
            lastOutputSequenceOf = Observation::tail,
            failureOf = Observation::failure,
            retryDelaysMillis = listOf(0L),
            timeoutMillis = 1_000L,
            pause = { },
        )

        assertNull(result.snapshot)
        assertNull(result.failure)
        assertEquals(2, attempts)
    }

    @Test
    fun readinessExhaustionWithoutAFailureStillUsesTheExistingFallback() = runBlocking {
        data class Observation(val tail: ULong)
        var attempts = 0

        val result = awaitTerminalSnapshotReadiness<Observation, String>(
            latestObservation = {
                attempts += 1
                Observation(tail = 52uL)
            },
            snapshotOf = { null },
            lastOutputSequenceOf = Observation::tail,
            failureOf = { null },
            retryDelaysMillis = listOf(0L),
            timeoutMillis = 1_000L,
            pause = { },
        )

        assertNull(result.snapshot)
        assertNull(result.failure)
        assertEquals(2, attempts)
        throwTerminalSnapshotReadinessFailureUnlessUnsupported(result.failure)
    }

    @Test
    fun callerCancellationPropagatesFromTerminalSnapshotReadiness() {
        data class Observation(val tail: ULong)
        val cancellation = CancellationException("terminal snapshot readiness cancelled")

        val thrown = assertThrows(CancellationException::class.java) {
            runBlocking {
                awaitTerminalSnapshotReadiness<Observation, String>(
                    latestObservation = { throw cancellation },
                    snapshotOf = { null },
                    lastOutputSequenceOf = Observation::tail,
                    failureOf = { null },
                    retryDelaysMillis = listOf(100L),
                    timeoutMillis = 1_000L,
                    pause = { error("cancelled readiness must not pause") },
                )
            }
        }

        assertEquals("terminal snapshot readiness cancelled", thrown.message)
    }

    @Test
    fun authenticationGenerationChangeCannotBecomeARetryableSnapshotFailure() {
        data class Observation(val tail: ULong)
        val authentication = AuthenticationLifecycleFence()
        val capturedGeneration = authentication.capture()
        var attempts = 0

        val thrown = assertThrows(IllegalStateException::class.java) {
            runBlocking {
                awaitTerminalSnapshotReadiness<Observation, String>(
                    latestObservation = {
                        attempts += 1
                        authentication.invalidate()
                        throw java.net.SocketTimeoutException("old account request retired")
                    },
                    snapshotOf = { null },
                    lastOutputSequenceOf = Observation::tail,
                    failureOf = { null },
                    retryDelaysMillis = listOf(0L, 0L),
                    timeoutMillis = 1_000L,
                    pause = { error("a retired authentication generation must not retry") },
                    ensureObservationContext = {
                        check(authentication.isCurrent(capturedGeneration)) {
                            "Authentication lifecycle changed"
                        }
                    },
                    observationFailure = {
                        SessionOpenFailure(SessionOpenFailureCode.SERVICE_TIMEOUT)
                    },
                )
            }
        }

        assertEquals("Authentication lifecycle changed", thrown.message)
        assertEquals(1, attempts)
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

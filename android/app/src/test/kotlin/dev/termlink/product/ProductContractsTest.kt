package dev.termlink.product

import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.network.direct.ProductionDirectCarrier
import dev.termlink.network.direct.ProductionDirectNetworkCheckKind
import dev.termlink.network.direct.ProductionDirectNetworkCheckResult
import dev.termlink.network.direct.ProductionDirectRoutingPolicy
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.protocol.realtime.v1.SessionUpsertPayload
import dev.termlink.session.InputSubmission
import dev.termlink.session.RemoteLifecycle
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.session.RemoteSessionState
import java.io.IOException
import java.time.Instant
import java.util.Base64
import java.util.UUID
import java.util.concurrent.CompletableFuture
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import okhttp3.OkHttpClient

class ProductContractsTest {
    @Test
    fun directDataPlaneHelloCapabilitiesAreOptionalAndGatedTogether() {
        assertEquals(setOf("host_history_v1", "host_session_catalog_v1", "host_session_cleanup_v1"), productRealtimeOptionalCapabilities(false))
        assertEquals(
            setOf(
                "host_history_v1",
                "host_session_catalog_v1", "host_session_cleanup_v1",
                "direct_session_cutover_v1",
                "direct_transport_data_v1",
            ),
            productRealtimeOptionalCapabilities(true),
        )
    }

    @Test
    fun hostOwnedHistoryRequiresTheWindowsAuthorityCapability() {
        assertFalse(hasHostHistoryAuthority(emptySet()))
        assertTrue(hasHostHistoryAuthority(setOf(PcCapabilityIds.HOST_HISTORY_V1)))
        assertEquals("TL-H125", SessionOpenFailureCode.HOST_HISTORY_REQUIRED.stableCode)
        assertFalse(SessionOpenFailureCode.HOST_HISTORY_REQUIRED.retryable)
    }

    @Test
    fun publicDirectCarriersMapToTheProductProjectionWithoutGrantingAuthority() {
        assertEquals(PcCarrierState.RELAY, ProductionDirectCarrier.RELAY.toPcCarrierState())
        assertEquals(PcCarrierState.NEGOTIATING_DIRECT, ProductionDirectCarrier.NEGOTIATING.toPcCarrierState())
        assertEquals(PcCarrierState.LAN_DIRECT, ProductionDirectCarrier.LAN_DIRECT.toPcCarrierState())
        assertEquals(PcCarrierState.NAT_DIRECT, ProductionDirectCarrier.NAT_DIRECT.toPcCarrierState())
    }

    @Test
    fun serverDirectPolicyAndForcedCheckResultsMapWithoutLeakingDeviceIdsIntoUiKeys() {
        val deviceId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        assertEquals(DirectRoutingPolicy.AUTO, ProductionDirectRoutingPolicy.AUTO.toProductPolicy())
        assertEquals(DirectRoutingPolicy.FAILED, ProductionDirectRoutingPolicy.FAILED.toProductPolicy())
        assertEquals(DirectRoutingPolicy.UNAVAILABLE, ProductionDirectRoutingPolicy.UNAVAILABLE.toProductPolicy())
        assertEquals(
            DirectRoutingPolicy.RELAY_ONLY,
            ProductionDirectRoutingPolicy.RELAY_ONLY.toProductPolicy(),
        )

        val lan = emptyMap<UUID, DirectNetworkCheckState>().mergeNetworkChecks(
            mapOf(
                (deviceId to ProductionDirectNetworkCheckKind.LAN) to ProductionDirectNetworkCheckResult(
                    ProductionDirectNetworkCheckKind.LAN,
                    available = true,
                ),
            ),
            mapOf(deviceId to pcId),
        )
        val both = lan.mergeNetworkChecks(
            mapOf(
                (deviceId to ProductionDirectNetworkCheckKind.LAN) to ProductionDirectNetworkCheckResult(
                    ProductionDirectNetworkCheckKind.LAN, available = true,
                ),
                (deviceId to ProductionDirectNetworkCheckKind.NAT) to ProductionDirectNetworkCheckResult(
                    ProductionDirectNetworkCheckKind.NAT,
                    available = false,
                ),
            ),
            mapOf(deviceId to pcId),
        )

        assertEquals(DirectNetworkCheckStatus.AVAILABLE, both.getValue(pcId).lan)
        assertEquals(DirectNetworkCheckStatus.NOT_ESTABLISHED, both.getValue(pcId).nat)
        assertFalse(deviceId in both)
        assertTrue(both.mergeNetworkChecks(emptyMap(), mapOf(deviceId to pcId)).isEmpty())
        val repeated = both.mergeNetworkChecks(mapOf(
            (deviceId to ProductionDirectNetworkCheckKind.NAT) to ProductionDirectNetworkCheckResult(
                ProductionDirectNetworkCheckKind.NAT, available = false, checking = true, requestId = 2,
            )), mapOf(deviceId to pcId))
        assertEquals(DirectNetworkCheckStatus.CHECKING, repeated.getValue(pcId).nat)
        assertEquals(DirectNetworkCheckStatus.UNKNOWN, repeated.getValue(pcId).lan)
    }

    @Test
    fun directRuntimeProjectionRequiresTheExactOwnerMultiplexerAndCoordinator() {
        val owner = Any()
        val multiplexer = Any()
        val coordinator = Any()

        assertTrue(
            directRuntimeProjectionClaimMatches(
                owner,
                multiplexer,
                coordinator,
                owner,
                multiplexer,
                coordinator,
            ),
        )
        assertFalse(
            directRuntimeProjectionClaimMatches(
                owner,
                multiplexer,
                Any(),
                owner,
                multiplexer,
                coordinator,
            ),
        )
    }

    @Test
    fun directCarrierProjectionKeepsOnlyTrustedOnlinePcDevicesAndOmitsRelay() {
        val onlineDirect = UUID.randomUUID()
        val offline = UUID.randomUUID()
        val revoked = UUID.randomUUID()
        val explicitRelay = UUID.randomUUID()
        val devices = listOf(
            directDevice(onlineDirect, DeviceTrustState.TRUSTED),
            directDevice(offline, DeviceTrustState.TRUSTED),
            directDevice(revoked, DeviceTrustState.REVOKED),
            directDevice(explicitRelay, DeviceTrustState.TRUSTED),
        )
        val pcs = listOf(
            directPc(onlineDirect, online = true),
            directPc(offline, online = false),
            directPc(revoked, online = true),
            directPc(explicitRelay, online = true),
        )

        assertEquals(
            mapOf(onlineDirect to PcCarrierState.NAT_DIRECT),
            retainAuthoritativePcCarrierStates(
                carrierStates = mapOf(
                    onlineDirect to PcCarrierState.NAT_DIRECT,
                    offline to PcCarrierState.LAN_DIRECT,
                    revoked to PcCarrierState.NEGOTIATING_DIRECT,
                    explicitRelay to PcCarrierState.RELAY,
                    UUID.randomUUID() to PcCarrierState.LAN_DIRECT,
                ),
                devices = devices,
                pcs = pcs,
            ),
        )
    }

    @Test
    fun directSessionCarrierProjectionNeverExpandsOnePcPathToUnlistedSessions() {
        val deviceId = UUID.randomUUID()
        val pc = directPc(deviceId, online = true)
        val directSession = directSession(pc.id, online = true)
        val lateSession = directSession(pc.id, online = true)
        val offlineSession = directSession(pc.id, online = false)

        assertEquals(
            mapOf(directSession.id to PcCarrierState.LAN_DIRECT),
            retainAuthoritativeSessionCarrierStates(
                carrierStates = mapOf(
                    directSession.id to PcCarrierState.LAN_DIRECT,
                    lateSession.id to PcCarrierState.RELAY,
                    offlineSession.id to PcCarrierState.NAT_DIRECT,
                    UUID.randomUUID() to PcCarrierState.NAT_DIRECT,
                ),
                devices = listOf(directDevice(deviceId, DeviceTrustState.TRUSTED)),
                pcs = listOf(pc),
                sessions = listOf(directSession, lateSession, offlineSession),
            ),
        )
    }

    @Test
    fun directShadowUsesAnIndependentHttpResourceDomainFromRelay() {
        val relay = OkHttpClient()
        val direct = createIsolatedDirectShadowHttpClient(relay)
        try {
            assertNotSame(relay.dispatcher, direct.dispatcher)
            assertNotSame(relay.connectionPool, direct.connectionPool)
        } finally {
            relay.dispatcher.executorService.shutdownNow()
            relay.connectionPool.evictAll()
            direct.dispatcher.executorService.shutdownNow()
            direct.connectionPool.evictAll()
        }
    }

    @Test
    fun realtimeForegroundLifecycleKeepsEarliestBackgroundAndDeduplicatesGeneration() {
        val initial = observeRealtimeAppForeground(RealtimeAppLifecycleState(), 1, 1_000)
        assertTrue(initial.accepted)
        assertEquals(null, initial.backgroundDurationMillis)

        val firstStop = recordRealtimeAppBackground(initial.state, 2_000)
        val duplicateStop = recordRealtimeAppBackground(firstStop, 5_000)
        assertEquals(2_000L, duplicateStop.earliestBackgroundAtMillis)

        val resumed = observeRealtimeAppForeground(duplicateStop, 2, 18_250)
        assertTrue(resumed.accepted)
        assertEquals(16_250L, resumed.backgroundDurationMillis)
        assertEquals(null, resumed.state.earliestBackgroundAtMillis)

        val duplicateForeground = observeRealtimeAppForeground(resumed.state, 2, 19_000)
        assertFalse(duplicateForeground.accepted)
        assertEquals(resumed.state, duplicateForeground.state)
    }

    @Test
    fun delayedBackgroundCannotMoveLifecycleBehindLatestForeground() {
        val foreground = observeRealtimeAppForeground(RealtimeAppLifecycleState(), 4, 10_000).state
        assertEquals(foreground, recordRealtimeAppBackground(foreground, 9_999))
    }

    @Test
    fun nativeCheckpointCanResumeBehindAckOnlyByReplayingItsSuffix() {
        assertTrue(snapshotCanResumeThroughDurableAcknowledgement(42uL, 42uL))
        assertTrue(snapshotCanResumeThroughDurableAcknowledgement(41uL, 42uL))
        assertFalse(snapshotCanResumeThroughDurableAcknowledgement(43uL, 42uL))
    }

    @Test
    fun snapshotSuffixMustReachOriginalAckBeforeRealtimeSubscribe() {
        assertFalse(snapshotSuffixRestoreReady(8_999uL, 9_000uL, 10_000uL))
        assertTrue(snapshotSuffixRestoreReady(9_000uL, 9_000uL, 10_000uL))
        assertTrue(snapshotSuffixRestoreReady(9_001uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreReady(6_000uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreReady(9_000uL, 9_000uL, 20_000uL))
        assertTrue(snapshotSuffixRestoreReady(15_904uL, 9_000uL, 20_000uL))
        assertFalse(snapshotSuffixRestoreReady(15_903uL, 9_000uL, 20_000uL))
        assertTrue(snapshotSuffixRestoreIsBounded(5_904uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreIsBounded(5_903uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreIsBounded(9_001uL, 9_000uL, 10_000uL))
        assertFalse(snapshotSuffixRestoreIsBounded(9_000uL, 10_001uL, 10_000uL))
        assertTrue(fullPrefixReplayCanPreserveDurableAcknowledgement(0uL))
        assertFalse(fullPrefixReplayCanPreserveDurableAcknowledgement(1uL))
    }

    @Test
    fun durableNonOutputEventExtendsTheHostHistoryRestoreTarget() {
        assertEquals(46uL, restorableSessionEventTail(45uL, 46uL))
        assertEquals(46uL, restorableSessionEventTail(46uL, 45uL))
    }

    @Test
    fun snapshotSuffixBoundIsRecheckedAgainstMovingAuthorityAndReplayTargets() {
        assertTrue(snapshotSuffixRestoreIsBounded(100uL, 200uL, 4_100uL))
        assertFalse(snapshotSuffixRestoreIsBounded(100uL, 200uL, 6_000uL))
        assertTrue(snapshotSuffixReplayTargetIsBounded(100uL, 4_196uL))
        assertFalse(snapshotSuffixReplayTargetIsBounded(100uL, 4_197uL))
    }

    @Test
    fun realtimeNetworkCapabilityFallbackDeduplicatesAndReconnectsAfterLoss() {
        assertEquals(
            RealtimeNetworkObservationTransition.INITIAL,
            realtimeNetworkObservationTransition(false, null, "network-1"),
        )
        assertEquals(
            RealtimeNetworkObservationTransition.UNCHANGED,
            realtimeNetworkObservationTransition(true, "network-1", "network-1"),
        )
        assertEquals(
            RealtimeNetworkObservationTransition.RECONNECTED,
            realtimeNetworkObservationTransition(true, null, "network-1"),
        )
        assertEquals(
            RealtimeNetworkObservationTransition.RECONNECTED,
            realtimeNetworkObservationTransition(true, "network-1", "network-2"),
        )
    }

    @Test
    fun encryptedHistorySessionUpsertPreservesTitlePresenceAndLegacyLifecycle() {
        val title = productSessionUpsertEvent(
            41,
            SessionUpsertPayload.newBuilder()
                .setLifecycle("CHANGED")
                .setTitle("Codex — 构建")
                .build(),
        )
        assertTrue(title is RemoteSessionEvent.Title)
        assertEquals("Codex — 构建", (title as RemoteSessionEvent.Title).value)

        val cleared = productSessionUpsertEvent(
            42,
            SessionUpsertPayload.newBuilder()
                .setLifecycle("CHANGED")
                .setTitle("")
                .build(),
        )
        assertTrue(cleared is RemoteSessionEvent.Title)
        assertEquals("", (cleared as RemoteSessionEvent.Title).value)

        val legacy = productSessionUpsertEvent(
            43,
            SessionUpsertPayload.newBuilder().setLifecycle("INTERRUPTED").build(),
        )
        assertTrue(legacy is RemoteSessionEvent.Lifecycle)
        assertEquals(RemoteLifecycle.INTERRUPTED, (legacy as RemoteSessionEvent.Lifecycle).state)
    }

    @Test
    fun commandSubmissionIsOneOrderedInputEndingInCarriageReturn() {
        assertEquals("ni TLFINAL123\r", commandInputBytes("ni TLFINAL123").decodeToString())
        assertEquals(null, commandSubmissionFailure(InputSubmission.Sent(UUID.randomUUID())))
        assertEquals(
            "Request control before sending a command",
            commandSubmissionFailure(InputSubmission.RejectedNoLease),
        )
        assertThrows(IllegalArgumentException::class.java) { commandInputBytes("") }
    }

    @Test
    fun selectedRunningSessionRequestsControlInitiallyAndAfterRealtimeReconnect() {
        assertEquals(
            SessionControlRequestTrigger.INITIAL_OPEN,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = null,
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Reconnecting(lastAckedSequence = 40),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.REALTIME_LIVE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 40),
            ),
        )
        // ACK progress keeps the same stable effect key, so terminal output
        // cannot create a lease-request loop.
        assertEquals(
            SessionControlRequestTrigger.REALTIME_LIVE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.Controlling(
                    lastAckedSequence = 41,
                    leaseId = UUID.randomUUID().toString(),
                    expiresAtEpochMillis = Long.MAX_VALUE,
                ),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.ReadOnly(
                    lastAckedSequence = 41,
                    reason = "transport reconnected without a live lease",
                ),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = true,
                online = false,
                remoteState = null,
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.NONE,
            sessionControlRequestTrigger(
                foreground = false,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.ReadOnly(
                    lastAckedSequence = 41,
                    reason = "control released",
                ),
            ),
        )
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(
                foreground = true,
                online = true,
                remoteState = RemoteSessionState.ReadOnly(
                    lastAckedSequence = 41,
                    reason = "LOCAL_INPUT_WON",
                ),
            ),
        )
    }

    @Test
    fun controlRequestEffectIsStableAcrossAckButAdvancesForEveryForegroundEntry() {
        val initial = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = 1,
            online = true,
            remoteState = null,
        )
        assertEquals(
            SessionControlRequestEffectKey(1, SessionControlRequestTrigger.INITIAL_OPEN),
            initial,
        )

        val liveAt40 = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = 1,
            online = true,
            remoteState = RemoteSessionState.Live(lastAckedSequence = 40),
        )
        val liveAt41 = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = 1,
            online = true,
            remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
        )
        assertEquals(liveAt40, liveAt41)
        assertEquals(
            null,
            sessionControlRequestEffectKey(
                foreground = false,
                foregroundGeneration = 1,
                online = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
            ),
        )

        val secondForegroundGeneration = nextForegroundGeneration(1)
        assertEquals(2L, secondForegroundGeneration)
        val resumed = sessionControlRequestEffectKey(
            foreground = true,
            foregroundGeneration = secondForegroundGeneration,
            online = true,
            remoteState = RemoteSessionState.Live(lastAckedSequence = 41),
        )
        assertTrue(resumed != liveAt41)
        assertEquals(3L, nextForegroundGeneration(secondForegroundGeneration))
    }

    @Test
    fun explicitlyReleasedControlStaysReadOnlyUntilRequestedFromTheMenu() {
        for (foregroundGeneration in listOf(1L, 2L)) {
            assertEquals(
                null,
                sessionControlRequestEffectKey(
                    foreground = true,
                    foregroundGeneration = foregroundGeneration,
                    online = true,
                    remoteState = RemoteSessionState.ReadOnly(41, "control released by user"),
                ),
            )
        }
        assertEquals(
            SessionControlRequestTrigger.FOREGROUND_RESUME,
            sessionControlRequestTrigger(true, true, RemoteSessionState.ReadOnly(41, "control released")),
        )
    }

    @Test
    fun failedBootstrapRetryReplacesOnlyTheInvalidInstalledClient() {
        val failed = RemoteSessionState.Reconnecting(
            lastAckedSequence = 40,
            reason = SESSION_BOOTSTRAP_FAILED_REASON,
        )
        assertTrue(shouldReplaceLiveSessionForOpen(liveSessionExists = true, remoteState = failed))
        assertFalse(shouldReplaceLiveSessionForOpen(liveSessionExists = false, remoteState = failed))
        assertFalse(
            shouldReplaceLiveSessionForOpen(
                liveSessionExists = true,
                remoteState = RemoteSessionState.Reconnecting(
                    lastAckedSequence = 40,
                    reason = SESSION_BOOTSTRAP_IN_PROGRESS_REASON,
                ),
            ),
        )
        assertFalse(
            shouldReplaceLiveSessionForOpen(
                liveSessionExists = true,
                remoteState = RemoteSessionState.Live(lastAckedSequence = 40),
            ),
        )
    }

    @Test
    fun initialBootstrapIsEstablishingAndDeviceBackRequiresASecondPress() {
        assertTrue(
            isEstablishingSessionConnection(
                RemoteSessionState.Reconnecting(
                    lastAckedSequence = 0,
                    reason = SESSION_BOOTSTRAP_IN_PROGRESS_REASON,
                ),
            ),
        )
        assertTrue(
            isEstablishingSessionConnection(
                RemoteSessionState.Reconnecting(
                    lastAckedSequence = 0,
                    reason = SESSION_BOOTSTRAP_FAILED_REASON,
                ),
            ),
        )
        assertFalse(
            isEstablishingSessionConnection(
                RemoteSessionState.Reconnecting(lastAckedSequence = 4, reason = "remote session interrupted"),
            ),
        )
        assertFalse(shouldExitAfterDeviceBack(nowMillis = 10_000L, armedAtMillis = null))
        assertTrue(shouldExitAfterDeviceBack(nowMillis = 11_999L, armedAtMillis = 10_000L))
        assertTrue(shouldExitAfterDeviceBack(nowMillis = 12_000L, armedAtMillis = 10_000L))
        assertFalse(shouldExitAfterDeviceBack(nowMillis = 12_001L, armedAtMillis = 10_000L))
    }

    @Test
    fun leaseHeldRetryOutlivesServerTtlButStopsAfterItsBoundedSchedule() = runBlocking {
        assertTrue(CONTROL_LEASE_HELD_RETRY_DELAYS_MILLIS.sum() > 30_000L)
        var requests = 0
        val pauses = mutableListOf<Long>()

        val acquired = acquireControlLeaseWithRetry(
            remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
            request = {
                requests += 1
                if (requests < 3) throw TermLinkApiException(409, "LEASE_HELD", traceId = null)
            },
            retryDelaysMillis = listOf(1_000L, 2_000L),
            pause = { pauses += it },
        )

        assertTrue(acquired)
        assertEquals(3, requests)
        assertEquals(listOf(1_000L, 2_000L), pauses)
    }

    @Test
    fun leaseRetryRejectsAuthorityFailuresAndSkipsAnExistingLease() {
        var requests = 0
        val failure = assertThrows(TermLinkApiException::class.java) {
            runBlocking {
                acquireControlLeaseWithRetry(
                    remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
                    request = {
                        requests += 1
                        throw TermLinkApiException(403, "CONTROL_NOT_GRANTED", traceId = null)
                    },
                    retryDelaysMillis = listOf(1L),
                    pause = { error("must not retry") },
                )
            }
        }
        assertEquals("CONTROL_NOT_GRANTED", failure.code)
        assertEquals(1, requests)

        val skipped = runBlocking {
            acquireControlLeaseWithRetry(
                remoteState = {
                    RemoteSessionState.Controlling(
                        lastAckedSequence = 50,
                        leaseId = UUID.randomUUID().toString(),
                        expiresAtEpochMillis = Long.MAX_VALUE,
                    )
                },
                request = { error("must not request an existing lease") },
            )
        }
        assertFalse(skipped)
    }

    @Test
    fun leaseRetryRecoversFromTransientHttpAndNetworkFailures() = runBlocking {
        var requests = 0
        val pauses = mutableListOf<Long>()

        val acquired = acquireControlLeaseWithRetry(
            remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
            request = {
                requests += 1
                when (requests) {
                    1 -> throw TermLinkApiException(503, "LEASE_UNAVAILABLE", traceId = null)
                    2 -> throw IOException("network switched")
                }
            },
            retryDelaysMillis = emptyList(),
            transientRetryDelaysMillis = listOf(5L, 10L),
            pause = { pauses += it },
        )

        assertTrue(acquired)
        assertEquals(3, requests)
        assertEquals(listOf(5L, 10L), pauses)
    }

    @Test
    fun controllingRuntimeStateClearsAStaleSessionFailure() {
        val sessionId = UUID.randomUUID()
        val failure = SessionOpenFailure(SessionOpenFailureCode.SERVER_REJECTED)
        val failures = mapOf(sessionId to failure)

        assertEquals(
            failures,
            sessionFailuresAfterRuntimeState(
                failures,
                sessionId,
                RemoteSessionState.ReadOnly(lastAckedSequence = 1, reason = "lease expired"),
            ),
        )
        assertTrue(
            sessionFailuresAfterRuntimeState(
                failures,
                sessionId,
                RemoteSessionState.Controlling(
                    lastAckedSequence = 1,
                    leaseId = UUID.randomUUID().toString(),
                    expiresAtEpochMillis = Long.MAX_VALUE,
                ),
            ).isEmpty(),
        )
    }

    @Test
    fun readOnlyCleanupDoesNotPruneAnInFlightLiveBootstrap() {
        val bootstrappingSession = UUID.randomUUID()
        val installedReadOnlySession = UUID.randomUUID()
        val installedLiveSession = UUID.randomUUID()

        assertEquals(
            setOf(bootstrappingSession, installedReadOnlySession, installedLiveSession),
            retainedRuntimeMetadataSessionIds(
                authoritativeSessions = listOf(
                    SessionSummary(
                        id = bootstrappingSession,
                        pcId = UUID.randomUUID(),
                        shellType = "powershell-7",
                        encryptedTitle = null,
                        state = SessionState.RUNNING,
                        columns = 120,
                        rows = 30,
                        lastOutputSequence = 76uL,
                        online = true,
                        createdAt = Instant.EPOCH,
                        endedAt = null,
                    ),
                ),
                readOnlySessionIds = setOf(installedReadOnlySession),
                liveSessionIds = setOf(installedLiveSession),
            ),
        )
    }

    @Test
    fun leaseHeldRetryStopsAfterTheConfiguredAttempts() {
        var requests = 0
        val pauses = mutableListOf<Long>()
        val failure = assertThrows(TermLinkApiException::class.java) {
            runBlocking {
                acquireControlLeaseWithRetry(
                    remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
                    request = {
                        requests += 1
                        throw TermLinkApiException(409, "LEASE_HELD", traceId = null)
                    },
                    retryDelaysMillis = listOf(1L, 2L),
                    pause = { pauses += it },
                )
            }
        }

        assertEquals("LEASE_HELD", failure.code)
        assertEquals(3, requests)
        assertEquals(listOf(1L, 2L), pauses)
    }

    @Test
    fun leaseRetryPropagatesCancellationWithoutAnotherRequest() {
        val cancellation = CancellationException("session page left")
        var requests = 0
        val thrown = assertThrows(CancellationException::class.java) {
            runBlocking {
                acquireControlLeaseWithRetry(
                    remoteState = { RemoteSessionState.Live(lastAckedSequence = 50) },
                    request = {
                        requests += 1
                        throw TermLinkApiException(409, "LEASE_HELD", traceId = null)
                    },
                    retryDelaysMillis = listOf(1L),
                    pause = { throw cancellation },
                )
            }
        }

        assertTrue(thrown === cancellation)
        assertEquals(1, requests)
    }

    @Test
    fun serverUrlPolicyRequiresHttpsUnlessExplicitlyTestingCleartext() {
        assertEquals("https://example.com:8443", ServerUrlPolicy.normalize(" HTTPS://Example.com:8443/ "))
        assertEquals("http://127.0.0.1:8080", ServerUrlPolicy.normalize("http://127.0.0.1:8080", allowCleartextForLocalTest = true))
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("http://127.0.0.1:8080") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://user:pass@example.com") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://example.com/api") }
        assertThrows(IllegalArgumentException::class.java) { ServerUrlPolicy.normalize("https://example.com?debug=true") }
    }

    @Test
    fun pairingParserCanonicalizesCodeAndNonce() {
        val id = UUID.randomUUID()
        val nonceBytes = ByteArray(32) { it.toByte() }
        val nonce = Base64.getUrlEncoder().withoutPadding().encodeToString(nonceBytes)
        val fullWidthId = id.toString().replace('-', '\uFF0D')
        val parsed = PairingInputParser.parse(fullWidthId, " abcd-efgh ", nonce)

        assertEquals(id, parsed.challengeId)
        assertEquals("ABCDEFGH", parsed.code)
        assertEquals(nonce, parsed.challengeNonce)
        assertThrows(IllegalArgumentException::class.java) {
            PairingInputParser.parse(id.toString(), "AAAA-AAAA", "bad")
        }
    }

    @Test
    fun pendingAndTrustedDeviceResponsesRemainServerAuthoritative() {
        val id = UUID.randomUUID()
        val pending = parseDevice(id, "PENDING")
        val trusted = parseDevice(id, "TRUSTED")

        assertEquals(DeviceTrustState.PENDING, pending.trustState)
        assertEquals(DeviceTrustState.TRUSTED, trusted.trustState)
        assertEquals(id, pending.id)
        assertEquals(id, trusted.id)
    }

    @Test
    fun pcReachabilityUsesTheServerOnlineFieldInsteadOfLastSeenTime() {
        val onlineWithOldHeartbeat = Json.parseToJsonElement(
            """{"online":true,"last_seen_at":"2000-01-01T00:00:00Z"}""",
        ).jsonObject
        val offlineWithFutureHeartbeat = Json.parseToJsonElement(
            """{"online":false,"last_seen_at":"2999-01-01T00:00:00Z"}""",
        ).jsonObject
        val missingOnline = Json.parseToJsonElement(
            """{"last_seen_at":"2999-01-01T00:00:00Z"}""",
        ).jsonObject

        assertTrue(authoritativePcOnline(onlineWithOldHeartbeat))
        assertFalse(authoritativePcOnline(offlineWithFutureHeartbeat))
        assertFalse(authoritativePcOnline(missingOnline))
    }

    @Test
    fun emptyCatalogAcceptsLegacyNullAndProductionEmptyArray() {
        val legacyNull = Json.parseToJsonElement("""{"plans":null}""").jsonObject
        val emptyArray = Json.parseToJsonElement("""{"plans":[]}""").jsonObject
        val emptyItems = Json.parseToJsonElement("""{"items":null}""").jsonObject

        assertEquals(emptyList<Any>(), ProductResponseParser.catalogPlanObjects(legacyNull))
        assertEquals(emptyList<Any>(), ProductResponseParser.catalogPlanObjects(emptyArray))
        assertEquals(emptyList<Any>(), ProductResponseParser.itemObjects(emptyItems))
    }

    @Test
    fun authStateEnvelopeUsesCipherAndDoesNotStorePlaintext() {
        val cipher = FakeCipher()
        val plaintext = "refresh-token-secret".encodeToByteArray()

        val sealed = AuthStateEnvelope.seal(plaintext, cipher)

        assertFalse(sealed.contains("refresh-token-secret"))
        assertEquals(plaintext.toList(), AuthStateEnvelope.open(sealed, cipher).toList())
        assertEquals(1, cipher.encryptions)
        assertEquals(1, cipher.decryptions)
    }

    @Test
    fun reservedInputSequenceProviderAdvancesAcrossRangesWithoutReuse() {
        val ranges = ArrayDeque(listOf(101L..103L, 201L..202L))
        val provider = ProductionProductRepository.ReservedInputSequenceProvider {
            ranges.removeFirst()
        }

        assertEquals(listOf(101L, 102L, 103L, 201L, 202L), List(5) { provider.next() })
    }

    @Test
    fun tokenRefreshCoordinatorCallsRefreshAndRejectsBlankTokens() {
        runBlocking {
            var calls = 0
            val forced = mutableListOf<Boolean>()
            val coordinator = TokenRefreshCoordinator { force ->
                calls += 1
                forced += force
                "fresh-$calls"
            }

            assertEquals("fresh-1", coordinator.freshAccessToken())
            assertEquals("fresh-2", coordinator.freshAccessToken(force = true))
            assertEquals(2, calls)
            assertEquals(listOf(false, true), forced)
            assertThrows(IllegalArgumentException::class.java) {
                runBlocking { TokenRefreshCoordinator { _ -> " " }.freshAccessToken() }
            }
        }
    }

    @Test
    fun cancelledTokenRefreshWaiterDoesNotBlockTheSharedRefreshLane() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        var calls = 0
        val coordinator = TokenRefreshCoordinator { _ ->
            calls += 1
            if (calls == 1) {
                entered.complete(Unit)
                release.await()
            }
            "fresh-$calls"
        }

        val owner = async { coordinator.freshAccessToken() }
        entered.await()
        val waiter = async { coordinator.freshAccessToken() }
        waiter.cancelAndJoin()
        release.complete(Unit)

        assertEquals("fresh-1", owner.await())
        assertEquals("fresh-2", coordinator.freshAccessToken())
        assertEquals(2, calls)
    }

    @Test
    fun remoteDownloadPreflightTimeoutIsRetryableAndBilingual() = runBlocking {
        val never = CompletableDeferred<Unit>()
        val failure = runCatching {
            boundedRemoteDownloadPreflight(timeoutMillis = 25L) { never.await() }
        }.exceptionOrNull()

        assertTrue(failure is RemoteFileTransferException)
        assertTrue((failure as RemoteFileTransferException).retryable)
        assertTrue(failure.message.orEmpty().contains("timed out"))
        assertTrue(failure.message.orEmpty().contains("超时"))
    }

    @Test
    fun localFileTransferFailuresAreTerminalButCancellationIsPreserved() = runBlocking {
        listOf(IOException("disk full"), SecurityException("permission denied")).forEach { source ->
            val failure = runCatching {
                localFileTransferStage<Unit> { throw source }
            }.exceptionOrNull()
            assertTrue(failure is RemoteFileTransferException)
            assertFalse((failure as RemoteFileTransferException).retryable)
            assertTrue(failure.cause === source)
        }

        val cancellation = CancellationException("cancelled")
        assertTrue(runCatching {
            localFileTransferStage<Unit> { throw cancellation }
        }.exceptionOrNull() === cancellation)
    }

    @Test
    fun authenticationLifecycleFenceRejectsRefreshCapturedBeforeSignOutOrClose() {
        val fence = AuthenticationLifecycleFence()
        val beforeSignOut = fence.capture()

        fence.invalidate()

        assertFalse(fence.isCurrent(beforeSignOut))
        val afterSignOut = fence.capture()
        assertTrue(fence.isCurrent(afterSignOut))

        fence.invalidate()
        assertFalse(fence.isCurrent(afterSignOut))
    }

    @Test
    fun authenticationExpiryClearsTheVisibleAccountIdentity() {
        val directDeviceId = UUID.randomUUID()
        val expired = authenticationExpiredSnapshot(
            snapshot = ProductSnapshot(
                signedIn = true,
                accountLabel = "alice@example.test",
                authenticationState = ProductAuthenticationState.AUTHENTICATED,
                connectionState = ProductConnectionState.CONNECTED,
                pcCarrierStates = mapOf(directDeviceId to PcCarrierState.LAN_DIRECT),
                sessionCarrierStates = mapOf(UUID.randomUUID() to PcCarrierState.LAN_DIRECT),
            ),
            message = "Authentication expired",
        )

        assertFalse(expired.signedIn)
        assertEquals(null, expired.accountLabel)
        assertEquals(ProductAuthenticationState.SIGNED_OUT, expired.authenticationState)
        assertEquals(ProductConnectionState.ERROR, expired.connectionState)
        assertEquals("Authentication expired", expired.connectionError)
        assertTrue(expired.pcCarrierStates.isEmpty())
        assertTrue(expired.sessionCarrierStates.isEmpty())
    }

    @Test
    fun onlyRejectedRefreshCredentialsExpireAnEstablishedAuthentication() {
        assertTrue(
            isAuthenticationRefreshRejection(
                NativeOidcException(NativeOidcFailure.INVALID_CREDENTIALS),
            ),
        )
        assertFalse(
            isAuthenticationRefreshRejection(
                NativeOidcException(NativeOidcFailure.NETWORK),
            ),
        )
        assertFalse(isAuthenticationRefreshRejection(IllegalStateException("temporary failure")))
    }

    @Test
    fun outerRefreshFailureCannotReplaceThePublishedAuthenticationExpiry() {
        val message = "Authentication expired"
        val expired = authenticationExpiredSnapshot(
            snapshot = ProductSnapshot(
                signedIn = true,
                authenticationState = ProductAuthenticationState.AUTHENTICATED,
                connectionState = ProductConnectionState.CONNECTED,
            ),
            message = message,
        )

        assertTrue(shouldPreserveAuthenticationExpiredFailure(expired, message))
        assertFalse(
            shouldPreserveAuthenticationExpiredFailure(
                expired.copy(
                    authenticationState = ProductAuthenticationState.NATIVE_CREDENTIALS,
                    connectionError = null,
                ),
                message,
            ),
        )
    }

    @Test
    fun realtimeTokenSourcePublishesTheLatestRefreshedTokenAndClearsOnSignOut() {
        val source = RealtimeAccessTokenSource()
        val provider = AccessTokenProvider(source::requireCurrent)

        assertThrows(IllegalStateException::class.java, source::requireCurrent)
        assertEquals("fresh-1", source.publish("fresh-1"))
        assertEquals("fresh-1", provider.accessToken())
        assertEquals("fresh-2", source.publish("fresh-2"))
        assertEquals("fresh-2", CompletableFuture.supplyAsync(provider::accessToken).get())

        source.clear()
        assertThrows(IllegalStateException::class.java, provider::accessToken)
    }

    private fun parseDevice(id: UUID, trustState: String) = ProductResponseParser.device(
        Json.parseToJsonElement(
            """
            {
              "id":"$id",
              "type":"ANDROID",
              "name":"Test phone",
              "key_id":"device-key",
              "exchange_public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "signing_public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
              "trust_state":"$trustState",
              "created_at":"2026-08-04T00:00:00Z"
            }
            """.trimIndent(),
        ).jsonObject,
    )

    private fun directDevice(
        id: UUID,
        trustState: DeviceTrustState,
    ): DeviceSummary = DeviceSummary(
        id = id,
        type = DeviceType.PC_AGENT,
        name = "pc",
        keyId = "key",
        exchangePublicKey = ByteArray(32),
        signingPublicKey = ByteArray(32),
        trustState = trustState,
        createdAt = Instant.EPOCH,
    )

    private fun directPc(
        deviceId: UUID,
        online: Boolean,
    ): PcSummary = PcSummary(
        id = UUID.randomUUID(),
        deviceId = deviceId,
        name = "pc",
        agentVersion = "test",
        remoteRoutingEnabled = true,
        online = online,
        lastSeenAt = Instant.EPOCH,
    )

    private fun directSession(pcId: UUID, online: Boolean): SessionSummary = SessionSummary(
        id = UUID.randomUUID(),
        pcId = pcId,
        shellType = "powershell",
        encryptedTitle = null,
        state = SessionState.RUNNING,
        columns = 80,
        rows = 24,
        lastOutputSequence = 0u,
        createdAt = Instant.EPOCH,
        endedAt = null,
        online = online,
    )

    private class FakeCipher : AuthStateCipher {
        var encryptions = 0
        var decryptions = 0

        override fun transform(input: ByteArray, encrypt: Boolean): ByteArray {
            if (encrypt) encryptions++ else decryptions++
            return if (encrypt) {
                ByteArray(12) { 0x5A } + input.map { (it.toInt() xor 0xA5).toByte() }.toByteArray()
            } else {
                input.copyOfRange(12, input.size).map { (it.toInt() xor 0xA5).toByte() }.toByteArray()
            }
        }
    }
}

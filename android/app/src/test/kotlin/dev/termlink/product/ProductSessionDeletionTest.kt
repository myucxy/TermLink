package dev.termlink.product

import dev.termlink.model.HistoryObject
import dev.termlink.model.DeviceSummary
import dev.termlink.model.DeviceTrustState
import dev.termlink.model.DeviceType
import dev.termlink.model.PcSummary
import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.network.production.ProductionHostHistoryNotReadyException
import dev.termlink.session.RemoteSessionState
import java.io.IOException
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductSessionDeletionTest {
    @Test
    fun cleanupSelectsOnlyConfirmedInactiveSessionsOnTheRequestedTrustedOnlinePc() {
        val exited = session()
        val interrupted = session().copy(pcId = exited.pcId, state = SessionState.INTERRUPTED)
        val running = session().copy(pcId = exited.pcId, state = SessionState.RUNNING)
        val inconsistent = interrupted.copy(id = UUID.randomUUID(), online = true)
        val snapshot = cleanupSnapshot(listOf(exited, interrupted, running, inconsistent, session()))
        assertEquals(listOf(exited.id, interrupted.id), snapshot.inactiveSessionIds(exited.pcId))
        val pc = snapshot.pcs.single()
        listOf(
            snapshot.copy(signedIn = false),
            snapshot.copy(connectionState = ProductConnectionState.ERROR),
            snapshot.copy(pcSessionCatalogErrors = mapOf(pc.id to "TIMEOUT")),
            snapshot.copy(pcs = listOf(pc.copy(online = false))),
            snapshot.copy(pcs = listOf(pc.copy(remoteRoutingEnabled = false))),
            snapshot.copy(pcs = listOf(pc.copy(capabilities = setOf(PcCapabilityIds.HOST_HISTORY_V1)))),
            snapshot.copy(devices = snapshot.devices.map { it.copy(trustState = DeviceTrustState.REVOKED) }),
        ).forEach { assertTrue(it.inactiveSessionIds(pc.id).isEmpty()) }
    }

    @Test
    fun nativeCleanupUsesFourBatchesFor245SessionsAndKeepsTheConfirmedSelection() = runBlocking {
        val first = session()
        val sessions = (1..245).map { session().copy(pcId = first.pcId) }
        val extra = session().copy(pcId = first.pcId)
        val state = MutableStateFlow(cleanupSnapshot(sessions + extra))
        val calls = mutableListOf<List<UUID>>()
        val deleted = mutableListOf<UUID>()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = state
            override suspend fun deleteSession(sessionId: UUID) = error("per-session path must not run")
            override suspend fun deleteInactiveSessionBatch(pcId: UUID, sessionIds: List<UUID>): Map<UUID, String> {
                calls += sessionIds
                return sessionIds.associateWith {
                    when (it) { sessions[0].id -> "FAILED"; sessions[1].id -> "SKIPPED_ACTIVE"; else -> "DELETED" }
                }
            }
        }
        val result = repository.deleteInactiveSessions(first.pcId, sessions.map { it.id }, { deleted += it }, {})
        assertEquals(listOf(64, 64, 64, 53), calls.map { it.size })
        assertEquals(InactiveSessionCleanupResult(deleted = 243, failed = 1, skipped = 1), result)
        assertFalse(calls.flatten().contains(extra.id))
        assertEquals(243, deleted.size)
    }

    @Test
    fun nativeCleanupStopsAfterUnconfirmedBatchWithoutRepeatedTimeouts() = runBlocking {
        val first = session()
        val sessions = (1..130).map { session().copy(pcId = first.pcId) }
        var calls = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = MutableStateFlow(cleanupSnapshot(sessions))
            override suspend fun deleteInactiveSessionBatch(pcId: UUID, sessionIds: List<UUID>): Map<UUID, String> {
                calls++
                throw IOException("bridge unavailable")
            }
        }
        val result = repository.deleteInactiveSessions(first.pcId, sessions.map { it.id }, { error("unconfirmed deletion") }, {})
        assertEquals(1, calls)
        assertEquals(InactiveSessionCleanupResult(failed = 64, skipped = 66, interrupted = true), result)
    }

    @Test
    fun nativeCleanupReportsUpgradeWithoutPretendingTheServerIsOffline() = runBlocking {
        val session = session()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = MutableStateFlow(cleanupSnapshot(listOf(session)))
            override suspend fun deleteInactiveSessionBatch(pcId: UUID, sessionIds: List<UUID>): Map<UUID, String> {
                throw dev.termlink.network.production.HostDeviceUpgradeRequiredException("host_session_cleanup_v1")
            }
        }
        val result = repository.deleteInactiveSessions(session.pcId, listOf(session.id), { error("unconfirmed deletion") }, {})
        assertEquals(InactiveSessionCleanupResult(failed = 1, interrupted = true, upgradeRequired = true), result)
    }

    @Test
    fun nativeCleanupSettlesInFlightBatchAndCancelsBeforeNextBatch() = runBlocking {
        val first = session()
        val sessions = (1..65).map { session().copy(pcId = first.pcId) }
        val state = MutableStateFlow(cleanupSnapshot(sessions))
        val entered = kotlinx.coroutines.CompletableDeferred<Unit>()
        val release = kotlinx.coroutines.CompletableDeferred<Unit>()
        var calls = 0
        var deleted = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override val snapshot = state
            override suspend fun deleteInactiveSessionBatch(pcId: UUID, sessionIds: List<UUID>): Map<UUID, String> {
                calls++
                entered.complete(Unit)
                release.await()
                return sessionIds.associateWith { "DELETED" }
            }
        }
        val job = launch { repository.deleteInactiveSessions(first.pcId, sessions.map { it.id }, { deleted++ }, {}) }
        entered.await()
        job.cancel()
        release.complete(Unit)
        job.join()
        assertEquals(1, calls)
        assertEquals(64, deleted)
        assertTrue(job.isCancelled)
    }
    private fun cleanupSnapshot(sessions: List<SessionSummary>): ProductSnapshot {
        val pc = PcSummary(sessions.first().pcId, UUID.randomUUID(), "PC", "1", remoteRoutingEnabled = true,
            online = true, lastSeenAt = null,
            capabilities = setOf(PcCapabilityIds.HOST_HISTORY_V1, PcCapabilityIds.HOST_SESSION_CATALOG_V1))
        return ProductSnapshot(
            signedIn = true, connectionState = ProductConnectionState.CONNECTED, pcs = listOf(pc), sessions = sessions,
            devices = listOf(DeviceSummary(pc.deviceId, DeviceType.WINDOWS_PC, "PC", "key", ByteArray(32),
                trustState = DeviceTrustState.TRUSTED, createdAt = Instant.EPOCH)),
        )
    }

    @Test
    fun hostHistoryDeleteRunsAgentAckThenRestThenLocalCleanup() = runBlocking {
        val calls = mutableListOf<String>()

        deleteHostHistorySessionAfterTermination(
            terminateRemote = { calls += "agent-ack" },
            deleteRemote = { calls += "rest-delete" },
            deleteLocal = { calls += "local-cleanup" },
        )

        assertEquals(listOf("agent-ack", "rest-delete", "local-cleanup"), calls)
    }

    @Test
    fun rejectedOrTimedOutHostTerminationPreservesRestAndLocalState() {
        listOf(
            IllegalStateException("Agent rejected termination"),
            IOException("termination acknowledgement timed out"),
        ).forEach { failure ->
            val calls = mutableListOf<String>()
            val thrown = assertThrows(failure.javaClass) {
                runBlocking {
                    deleteHostHistorySessionAfterTermination(
                        terminateRemote = {
                            calls += "terminate"
                            throw failure
                        },
                        deleteRemote = { calls += "rest" },
                        deleteLocal = { calls += "local" },
                    )
                }
            }
            assertSame(failure, thrown)
            assertEquals(listOf("terminate"), calls)
        }
    }

    @Test
    fun hostHistoryRestFailurePreservesLocalState() {
        val failure = IOException("server unavailable")
        val calls = mutableListOf<String>()
        val thrown = assertThrows(IOException::class.java) {
            runBlocking {
                deleteHostHistorySessionAfterTermination(
                    terminateRemote = { calls += "agent-ack" },
                    deleteRemote = {
                        calls += "rest-delete"
                        throw failure
                    },
                    deleteLocal = { calls += "local-cleanup" },
                )
            }
        }

        assertSame(failure, thrown)
        assertEquals(listOf("agent-ack", "rest-delete"), calls)
    }

    @Test
    fun hostHistoryNotReadyRetriesWithoutSwallowingTerminalFailure() = runBlocking {
        val pauses = mutableListOf<Long>()
        var attempts = 0
        val result = awaitHostHistoryReady(
            retryDelaysMillis = listOf(10L, 20L),
            pause = { pauses += it },
        ) {
            attempts += 1
            if (attempts < 3) throw ProductionHostHistoryNotReadyException("NOT_READY")
            "ready"
        }
        assertEquals("ready", result)
        assertEquals(3, attempts)
        assertEquals(listOf(10L, 20L), pauses)

        var terminalAttempts = 0
        assertThrows(IllegalStateException::class.java) {
            runBlocking {
                awaitHostHistoryReady(retryDelaysMillis = listOf(0L), pause = {}) {
                    terminalAttempts += 1
                    throw IllegalStateException("integrity failure")
                }
            }
        }
        assertEquals(1, terminalAttempts)
    }

    @Test
    fun successfulRemoteDeleteCleansOnlyTheSelectedSessionState() = runBlocking {
        val target = session()
        val retained = session()
        val targetHistory = history(target.id)
        val retainedHistory = history(retained.id)
        val activity = ActivityEntry("keep", Instant.EPOCH, "Keep", "Unrelated")
        var local = ProductSnapshot(
            sessions = listOf(target, retained),
            sessionStates = mapOf(
                target.id to RemoteSessionState.Live(4),
                retained.id to RemoteSessionState.Live(9),
            ),
            activity = listOf(activity),
            history = listOf(targetHistory, retainedHistory),
            replayedHistoryObjectIds = setOf(
                targetHistory.historyObject.id,
                retainedHistory.historyObject.id,
            ),
        )
        val calls = mutableListOf<String>()

        deleteSessionAfterRemoteSuccess(
            deleteRemote = { calls += "remote" },
            deleteLocal = {
                calls += "local"
                local = local.withoutSession(target.id)
            },
        )

        assertEquals(listOf("remote", "local"), calls)
        assertEquals(listOf(retained), local.sessions)
        assertEquals(mapOf(retained.id to RemoteSessionState.Live(9)), local.sessionStates)
        assertEquals(listOf(retainedHistory), local.history)
        assertEquals(setOf(retainedHistory.historyObject.id), local.replayedHistoryObjectIds)
        assertEquals(listOf(activity), local.activity)
    }

    @Test
    fun conflictPreservesTheExactLocalState() {
        assertRemoteFailurePreservesState(TermLinkApiException(409, "SESSION_ACTIVE", null))
    }

    @Test
    fun networkFailurePreservesTheExactLocalState() {
        assertRemoteFailurePreservesState(IOException("offline"))
    }

    @Test
    fun closeRetriesOnlyWhileWindowsStillReportsTheSessionActive() = runBlocking {
        val calls = mutableListOf<String>()
        var attempts = 0

        deleteClosedSessionAfterTermination(
            retryDelaysMillis = listOf(10L, 20L),
            pause = { calls += "pause:$it" },
            deleteRemote = {
                attempts += 1
                calls += "remote:$attempts"
                if (attempts < 3) {
                    throw TermLinkApiException(409, "SESSION_ACTIVE", traceId = null)
                }
            },
            deleteLocal = { calls += "local" },
        )

        assertEquals(
            listOf("remote:1", "pause:10", "remote:2", "pause:20", "remote:3", "local"),
            calls,
        )
    }

    @Test
    fun closeDoesNotHideAuthorizationOrTransportFailures() {
        val forbidden = TermLinkApiException(403, "DEVICE_FORBIDDEN", traceId = null)
        var localCalls = 0
        val thrown = assertThrows(TermLinkApiException::class.java) {
            runBlocking {
                deleteClosedSessionAfterTermination(
                    retryDelaysMillis = listOf(0L),
                    pause = { error("non-active failure must not retry") },
                    deleteRemote = { throw forbidden },
                    deleteLocal = { localCalls += 1 },
                )
            }
        }

        assertSame(forbidden, thrown)
        assertEquals(0, localCalls)
    }

    @Test
    fun closePreservesLocalSessionWhenActiveFenceDoesNotClear() {
        var attempts = 0
        var localCalls = 0
        val thrown = assertThrows(TermLinkApiException::class.java) {
            runBlocking {
                deleteClosedSessionAfterTermination(
                    retryDelaysMillis = listOf(0L, 0L),
                    pause = {},
                    deleteRemote = {
                        attempts += 1
                        throw TermLinkApiException(409, "SESSION_ACTIVE", traceId = null)
                    },
                    deleteLocal = { localCalls += 1 },
                )
            }
        }

        assertEquals("SESSION_ACTIVE", thrown.code)
        assertEquals(3, attempts)
        assertEquals(0, localCalls)
    }

    @Test
    fun unavailableRepositoryImplementsDeleteContract() {
        val repository = UnavailableProductRepository()
        assertThrows(IllegalStateException::class.java) {
            runBlocking { repository.deleteSession(UUID.randomUUID()) }
        }
    }

    private fun assertRemoteFailurePreservesState(failure: Throwable) {
        val original = ProductSnapshot(
            sessions = listOf(session()),
            activity = listOf(ActivityEntry("keep", Instant.EPOCH, "Keep", "Unrelated")),
        )
        var local = original
        var localCalls = 0

        val thrown = assertThrows(failure.javaClass) {
            runBlocking {
                deleteSessionAfterRemoteSuccess(
                    deleteRemote = { throw failure },
                    deleteLocal = {
                        localCalls += 1
                        local = ProductSnapshot()
                    },
                )
            }
        }

        assertSame(failure, thrown)
        assertSame(original, local)
        assertEquals(0, localCalls)
    }

    private fun session() = SessionSummary(
        id = UUID.randomUUID(),
        pcId = UUID.randomUUID(),
        shellType = "pwsh",
        encryptedTitle = null,
        state = SessionState.EXITED,
        columns = 120,
        rows = 30,
        lastOutputSequence = 9uL,
        createdAt = Instant.EPOCH,
        endedAt = Instant.EPOCH.plusSeconds(1),
    )

    private fun history(sessionId: UUID) = HistoryEntry(
        sessionId = sessionId,
        sessionLabel = "History",
        historyObject = HistoryObject(
            id = UUID.randomUUID(),
            kind = HistoryObject.Kind.CHUNK,
            objectKey = "history/object",
            keyId = "history-key",
            ciphertextSha256 = ByteArray(32) { 1 },
            sizeBytes = 1,
            startSequence = 1uL,
            endSequence = 1uL,
            snapshotSequence = null,
            formatName = null,
            formatVersion = null,
        ),
    )
}

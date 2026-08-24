package dev.termlink.product

import dev.termlink.model.HistoryObject
import dev.termlink.model.SessionState
import dev.termlink.model.SessionSummary
import dev.termlink.network.production.TermLinkApiException
import dev.termlink.session.RemoteSessionState
import java.io.IOException
import java.time.Instant
import java.util.UUID
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Test

class ProductSessionDeletionTest {
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

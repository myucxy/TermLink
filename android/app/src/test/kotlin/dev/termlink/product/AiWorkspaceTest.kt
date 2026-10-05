package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.launch
import kotlinx.coroutines.yield
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.awaitCancellation
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiWorkspaceTest {
    @Test fun recoveringQueuedTextPreservesExistingDraftAndRefusesOverflow() {
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("draft-review-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(UnavailableProductRepository(), scope, prefs)
            val item = AiQueued(UUID.randomUUID().toString(), "queued", "host", 1)
            workspace.queued["s"] = listOf(item); workspace.drafts["s"] = "new draft"
            workspace.cancelQueued("s", item)
            assertEquals("new draft\n\nqueued", workspace.drafts["s"])
            assertTrue(workspace.queued["s"].orEmpty().isEmpty())
            workspace.queued["s"] = listOf(item); workspace.drafts["s"] = "x".repeat(16000)
            workspace.cancelQueued("s", item)
            assertEquals(listOf(item), workspace.queued["s"])
            assertEquals(16000, workspace.drafts["s"]!!.length)
            assertFalse(workspace.appendDraft("s", "historic user message"))
            assertEquals(16000, workspace.drafts["s"]!!.length)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun missingReceiptOnOneComputerDoesNotBlockAnotherAndAllRequestsSurviveRestart() = runBlocking {
        val first = UUID.randomUUID(); val second = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("isolated-review-" + first, Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val requests = mutableListOf<Pair<UUID, String>>()
        var fail = true
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                requests += pcId to body
                if (JSONObject(body).getString("action") == "catalog") return """{"hostId":"$pcId","agents":[],"sessions":[]}"""
                if (fail) throw IOException("lost receipt")
                return """{"hostId":"$pcId"}"""
            }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(first); workspace.mutate(first, "create")!!.await()
            assertTrue(workspace.isBlocked(first)); assertFalse(workspace.isBlocked(second))
            workspace.refresh(second); assertTrue(second in workspace.catalogs)
            workspace.mutate(second, "create")!!.await()
            val originals = requests.filter { JSONObject(it.second).getString("action") == "create" }
            workspace.retire()
            val restored = AiWorkspace(repository, scope, prefs)
            assertEquals(setOf(first, second), restored.pendingPcs.toSet())
            fail = false
            restored.retryUncertain(first); restored.retryUncertain(second)
            assertEquals(originals, requests.takeLast(2))
            assertTrue(restored.pendingPcs.isEmpty()); assertFalse(prefs.contains("pending"))
            restored.retire()
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun directoryCreationIntentDoesNotStartSessionOrLeakIntoAnotherEntryPoint() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-create-directory-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var requests = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                requests++
                error("Opening a creation form must not create a session")
            }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            val pc = UUID.randomUUID()
            val favoritePc = UUID.randomUUID()
            workspace.beginCreate(pc, "D:\\项目目录\\My App")
            assertEquals(pc, workspace.createPc)
            assertEquals("D:\\项目目录\\My App", workspace.createDirectory)
            assertTrue(workspace.createPcLocked)
            workspace.createPc = null // Dismiss; a favorite may belong to a different PC.
            workspace.beginCreate(favoritePc, "E:\\favorite")
            assertEquals(favoritePc, workspace.createPc)
            assertEquals("E:\\favorite", workspace.createDirectory)
            workspace.createPc = null
            workspace.beginCreate(pc)
            assertNull(workspace.createDirectory) // Device card keeps managed-directory default.
            assertTrue(workspace.createPcLocked)
            workspace.createPc = null
            workspace.beginCreate(pc, lockPc = false)
            assertNull(workspace.createDirectory)
            assertFalse(workspace.createPcLocked)
            assertEquals(0, requests)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun catalogSummariesDriveAttentionAndCreationOpensChat(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val id = UUID.randomUUID().toString()
        val requests = mutableListOf<String>()
        var pendingCount = 1
        fun session() = JSONObject("""{"kind":"ai","id":"$id","agentName":"Claude Code","directory":"D:\\work","state":"idle","controlVersion":1,"canControl":true,"title":"排查登录流程","preview":"等待你的确认"}""").put("pendingCount", pendingCount)
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val action = JSONObject(body).getString("action")
                requests += action
                return if (action == "catalog") """{"hostId":"host","agents":[],"sessions":[${session()}]}"""
                else JSONObject().put("hostId", "host").put("session", session()).toString()
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-summary-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc)
            val metadata = workspace.conversations.getValue(id)
            assertEquals("排查登录流程", aiConversationTitle(metadata))
            assertTrue(aiNeedsAttention(metadata))
            workspace.conversations[id] = metadata.copy(messages = listOf(AiMessage("m", "assistant", "已有正文")),
                pending = listOf(AiPending("q", "question", "", emptyList(), true)))
            val answerDraft = AiQuestionDraft().apply { answers["scope"] = "保留 CLI"; choices["scope"] = setOf("UI") }
            workspace.questionDrafts[id to "q"] = answerDraft
            workspace.open(metadata)
            assertEquals("已有正文", workspace.conversations.getValue(id).messages.single().text)
            workspace.refresh(pc)
            assertSame(answerDraft, workspace.questionDrafts[id to "q"])
            pendingCount = 0
            workspace.refresh(pc)
            assertFalse(aiNeedsAttention(workspace.conversations.getValue(id)))
            assertTrue(workspace.questionDrafts.isEmpty())
            workspace.mutate(pc, "create")
            assertEquals(id, workspace.selected)
            assertEquals("D:\\work", workspace.preferredDirectory(pc))
            assertTrue(workspace.drafts.isEmpty())
            assertEquals(listOf("catalog", "catalog", "catalog", "create"), requests)
            workspace.questionDrafts[id to "q"] = answerDraft
            workspace.retire()
            assertTrue(workspace.questionDrafts.isEmpty())
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun requestTimeoutDoesNotKillPollingAndMutationRemainsRetryable(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        var timeout = true
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                if (timeout) withTimeout(1) { awaitCancellation() }
                return """{"hostId":"host","agents":[],"sessions":[]}"""
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-timeout-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        try {
            val workspace = AiWorkspace(repository, this, prefs)
            workspace.refresh(pc)
            assertTrue(workspace.unavailable[pc] == true)
            timeout = false
            workspace.refresh(pc)
            assertFalse(workspace.unavailable[pc] == true)
            timeout = true
            workspace.mutate(pc, "create")
            while (workspace.busy) yield()
            assertTrue(workspace.uncertain)
            timeout = false
            workspace.retryUncertain()
            while (workspace.busy) yield()
            assertFalse(workspace.uncertain)
            workspace.retire()
        } finally { prefs.edit().clear().commit() }
    }

    @Test fun delayedRefreshCannotOverwriteMutationReceipt(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val id = UUID.randomUUID().toString()
        fun session(state: String) = """{"kind":"ai","id":"$id","agentName":"fixture","directory":"D:\\work","state":"$state","controlVersion":1,"canControl":true}"""
        val delayed = CompletableDeferred<String>()
        var catalogs = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String =
                if (JSONObject(body).getString("action") == "catalog") {
                    if (++catalogs > 1) delayed.await()
                    else """{"hostId":"host","agents":[],"sessions":[${session("idle")}]}"""
                } else """{"hostId":"host","session":${session("running")}}"""
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-race-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc)
            val refresh = launch { workspace.refresh(pc) }
            yield()
            workspace.mutate(pc, "send", workspace.conversations.getValue(id), JSONObject().put("text", "hello"))
            delayed.complete("""{"hostId":"host","agents":[],"sessions":[${session("idle")}]}""")
            refresh.join()
            assertEquals("running", workspace.conversations.getValue(id).state)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun lostReceiptRetriesExactOperationAndCloseViewNeverEndsHost(): Unit = runBlocking {
        val requests = mutableListOf<String>()
        var loseReply = true
        val pc = UUID.randomUUID()
        val sessionId = UUID.randomUUID().toString()
        val session = """{"kind":"ai","id":"$sessionId","agentName":"fixture","directory":"D:\\work","state":"idle","error":"","controlVersion":1,"canControl":true,"messages":[],"pending":[],"truncated":false}"""
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                requests += body
                if (JSONObject(body).getString("action") == "catalog")
                    return """{"hostId":"host","agents":[],"sessions":[$session]}"""
                if (loseReply) { loseReply = false; throw IOException("lost reply") }
                return """{"hostId":"host","session":$session}"""
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-test-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc)
            val value = workspace.conversations.getValue(sessionId)
            workspace.open(value)
            workspace.drafts[sessionId] = "hello"
            workspace.mutate(pc, "send", value, JSONObject().put("text", "hello"))
            assertTrue(workspace.uncertain)
            assertEquals("hello", workspace.drafts[sessionId])
            workspace.refresh(pc)
            assertTrue(workspace.error.isNotEmpty())
            workspace.mutate(pc, "send", value, JSONObject().put("text", "another"))
            assertEquals(2, requests.size)
            // Process recreation recovers the exact encrypted request instead of inventing a new operation ID.
            val restored = AiWorkspace(repository, scope, prefs)
            assertTrue(restored.uncertain)
            restored.retryUncertain()
            assertEquals(requests[1], requests[2])
            assertFalse(restored.uncertain)
            val before = requests.size
            workspace.closeView(sessionId)
            assertEquals(before, requests.size)
            assertTrue(workspace.opened.isEmpty())
            workspace.retire()
            workspace.refresh(pc)
            assertEquals(before, requests.size)
            assertTrue(workspace.conversations.isEmpty())
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun endingSessionRemovesPhoneViewAndLeavesCatalogHistory() = runBlocking {
        val pc = UUID.randomUUID()
        val id = UUID.randomUUID().toString()
        val requests = mutableListOf<String>()
        fun session(state: String) = """{"kind":"ai","id":"$id","agentName":"fixture","agentId":"codex","directory":"D:\\work","state":"$state","error":"","controlVersion":1,"canControl":true,"capabilities":{"history":true,"resume":true}}"""
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val action = JSONObject(body).getString("action")
                requests += action
                return when (action) {
                    "catalog" -> """{"hostId":"host","agents":[],"sessions":[${session("idle")}] }"""
                    "end" -> """{"hostId":"host","session":${session("exited")}}"""
                    else -> error("unexpected action: $action")
                }
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-end-close-" + UUID.randomUUID(), Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc)
            val value = workspace.conversations.getValue(id)
            workspace.open(value)
            assertEquals(listOf(id), workspace.opened.map { it.sessionId })
            workspace.mutate(pc, "end", value)!!.await()
            assertTrue(workspace.opened.isEmpty())
            assertNull(workspace.conversations[id])
            assertEquals(id, workspace.catalogs.getValue(pc).sessions.single().id)
            assertEquals("exited", workspace.catalogs.getValue(pc).sessions.single().state)
            assertEquals(listOf("catalog", "end"), requests)
        } finally {
            scope.cancel()
            prefs.edit().clear().commit()
        }
    }

    @Test fun cliRouteAndAiRouteStayIndependent() {
        assertEquals(ProductPrimaryDestination.SESSIONS, ProductDestination.SESSIONS.primaryDestination())
        assertEquals(ProductDestination.SESSIONS, ProductPrimaryDestination.SESSIONS.destination())
        assertEquals(ProductDestination.AI, ProductPrimaryDestination.AI.destination())
        assertEquals(listOf("DEVICES", "SESSIONS", "AI", "SETTINGS"), ProductPrimaryDestination.entries.map { it.name })
    }
}

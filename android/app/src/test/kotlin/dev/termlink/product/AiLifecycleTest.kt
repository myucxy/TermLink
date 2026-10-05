package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiLifecycleTest {
    private val pc = UUID.randomUUID()
    private val id = UUID.randomUUID().toString()
    private fun session(state: String = "idle", control: Boolean = true, version: Long = 1) = JSONObject()
        .put("id", id).put("kind", "ai").put("agentId", "codex").put("agentName", "Codex")
        .put("directory", "D:\\work").put("state", state).put("title", "生命周期回归")
        .put("canControl", control).put("controlVersion", version)
        .put("capabilities", JSONObject().put("history", true).put("resume", true))
    private fun catalog(value: JSONObject) = """{"hostId":"host","agents":[],"sessions":[$value]}"""
    private fun reply(value: JSONObject) = """{"hostId":"host","session":$value}"""

    private fun checkWorkspace(handler: suspend (JSONObject) -> String, test: suspend (AiWorkspace, CoroutineScope, android.content.SharedPreferences) -> Unit) = runBlocking {
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("lifecycle-${UUID.randomUUID()}", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String) = handler(JSONObject(body))
        }
        val workspace = AiWorkspace(repo, scope, prefs)
        try { test(workspace, scope, prefs) }
        finally { workspace.retire(); scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun catalogOnlyRowsCanBeClosedWithoutEndingHostAndStayClosedAfterRefreshAndRestart() {
        val requests = mutableListOf<String>()
        checkWorkspace({ requests += it.getString("action"); catalog(session()) }) { workspace, scope, prefs ->
            workspace.refresh(pc)
            workspace.browseAgentGroup(pc, "codex")
            fun listed() = aiListEntries(workspace.opened, pc, workspace.catalogs[pc], true, workspace.closedSessionIds, workspace.conversations)
            assertTrue(workspace.opened.isEmpty())
            assertEquals(id, listed().single().sessionId) // Previously absent from bulk selection.
            val closedNotices = mutableListOf<String>()
            workspace.clearAttention = { closedNotices += it }
            listed().forEach { workspace.closeView(it.sessionId) }
            workspace.refresh(pc)
            assertTrue(listed().isEmpty())
            assertEquals(id, workspace.catalogs[pc]!!.sessions.single().id) // Still accessible in PC history.
            assertEquals(listOf("catalog", "catalog"), requests)
            assertEquals(listOf(id), closedNotices)
            val restored = AiWorkspace(UnavailableProductRepository(), scope, prefs)
            assertTrue(id in restored.closedSessionIds)
            assertTrue(aiListEntries(restored.opened, pc, workspace.catalogs[pc], true, restored.closedSessionIds).isEmpty())
            restored.retire()
            workspace.open(workspace.catalogs[pc]!!.sessions.single())
            assertEquals(id, listed().single().sessionId)
            assertFalse(id in workspace.closedSessionIds)
        }
    }

    @Test fun endedChatsStayInPcHistoryEvenWhenOpenedForReading() = checkWorkspace({ catalog(session("exited", false)) }) { workspace, _, _ ->
        workspace.refresh(pc)
        val historic = workspace.catalogs[pc]!!.sessions.single()
        workspace.open(historic)
        assertEquals(id, workspace.selected)
        assertTrue(aiListEntries(workspace.opened, pc, workspace.catalogs[pc], true, workspace.closedSessionIds, workspace.conversations).isEmpty())
        assertTrue(aiCatalogEntries(pc, workspace.catalogs[pc]).isEmpty())
        assertEquals("exited", workspace.catalogs[pc]!!.sessions.single().state)
    }

    @Test fun explicitEndAcquiresCurrentControlAndArchivesConfirmedState() {
        val actions = mutableListOf<JSONObject>()
        checkWorkspace({ request ->
            actions += request
            when (request.getString("action")) {
                "catalog" -> catalog(session(control = false))
                "control" -> reply(session(version = 2))
                "end" -> { assertEquals(2, request.getLong("controlVersion")); reply(session("exited", false, 2)) }
                else -> error("unexpected action")
            }
        }) { workspace, _, _ ->
            workspace.refresh(pc)
            val value = workspace.conversations.getValue(id)
            workspace.open(value)
            assertTrue(workspace.endSession(value).await())
            assertTrue(workspace.opened.isEmpty())
            assertNull(workspace.selected)
            assertEquals("exited", workspace.catalogs[pc]!!.sessions.single().state)
            assertEquals(listOf("catalog", "control", "end"), actions.map { it.getString("action") })
        }
    }

    @Test fun endWithLostReceiptKeepsViewUntilOriginalReceiptIsConfirmed() {
        var loseReply = true
        val requests = mutableListOf<String>()
        checkWorkspace({ request ->
            if (request.getString("action") == "catalog") catalog(session()) else {
                requests += request.toString()
                if (loseReply) throw IOException("lost receipt")
                reply(session("exited", false))
            }
        }) { workspace, _, _ ->
            workspace.refresh(pc)
            val value = workspace.conversations.getValue(id); workspace.open(value)
            assertFalse(workspace.endSession(value).await())
            assertTrue(workspace.isUncertain(pc)); assertEquals(id, workspace.opened.single().sessionId)
            loseReply = false; workspace.retryUncertain(pc)
            assertEquals(requests[0], requests[1])
            assertFalse(workspace.isBlocked(pc)); assertTrue(workspace.opened.isEmpty())
            assertEquals("exited", workspace.catalogs[pc]!!.sessions.single().state)
        }
    }

    @Test fun deleteFromHistoryWorksWithoutOpeningOrCachedConversation() = checkWorkspace({ request ->
        when (request.getString("action")) {
            "catalog" -> catalog(session("suspended", false))
            "forget" -> """{"hostId":"host","deletedSessionId":"$id"}"""
            else -> error("unexpected action")
        }
    }) { workspace, _, prefs ->
        workspace.refresh(pc)
        val value = workspace.catalogs[pc]!!.sessions.single()
        workspace.togglePin(id); workspace.closeView(id)
        assertNull(workspace.conversations[id]); assertTrue(workspace.opened.isEmpty())
        assertTrue(workspace.mutate(pc, "forget", value)!!.await())
        assertTrue(workspace.catalogs[pc]!!.sessions.isEmpty()); assertFalse(prefs.contains("pin:$id"))
    }

    @Test fun rejectedOrMismatchedDeleteDoesNotEraseLocalRecord() {
        listOf("""{"hostId":"host","error":"AGENT_BUSY"}""", """{"hostId":"host","deletedSessionId":"${UUID.randomUUID()}"}""").forEach { response ->
            checkWorkspace({ if (it.getString("action") == "catalog") catalog(session("exited", false)) else response }) { workspace, _, _ ->
                workspace.refresh(pc)
                val value = workspace.catalogs[pc]!!.sessions.single(); workspace.open(value)
                assertFalse(workspace.mutate(pc, "forget", value)!!.await())
                assertEquals(id, workspace.selected)
                assertEquals(id, workspace.catalogs[pc]!!.sessions.single().id)
            }
        }
    }
}

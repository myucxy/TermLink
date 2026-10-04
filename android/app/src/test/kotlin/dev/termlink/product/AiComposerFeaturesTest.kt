package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.*
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiComposerFeaturesTest {
    private val pc = UUID.randomUUID()
    private val id = UUID.randomUUID().toString()
    private fun session(state: String = "idle", control: Long = 1) = JSONObject()
        .put("kind", "ai").put("id", id).put("agentId", "claude").put("agentName", "Claude Code").put("directory", "D:\\work")
        .put("state", state).put("controlVersion", control).put("canControl", true).put("settingsVersion", 5).put("modelId", "old-model")
        .put("capabilities", JSONObject().put("configure", true).put("history", true)).put("historyBefore", 2)
    @Test fun configOnlyChangesAfterReceiptAndUnknownRequestReusesBody() = runBlocking {
        val requests = mutableListOf<String>()
        var lose = true
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                if (JSONObject(body).getString("action") == "catalog") return JSONObject().put("hostId", "host").put("agents", JSONArray()).put("sessions", JSONArray().put(session())).toString()
                requests += body
                if (lose) throw IOException()
                return JSONObject().put("hostId", "host").put("session", session().put("modelId", "new-model").put("settingsVersion", 6)).toString()
            }
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(repo, scope, prefs())
            workspace.refresh(pc); workspace.configure(workspace.conversations.getValue(id), model = "new-model")
            assertTrue(workspace.uncertain); assertEquals("old-model", workspace.conversations.getValue(id).modelId)
            assertEquals(5, JSONObject(requests.single()).getLong("expectedSettingsVersion"))
            lose = false; workspace.retryUncertain()
            assertEquals(2, requests.size); assertEquals(requests[0], requests[1]); assertEquals("new-model", workspace.conversations.getValue(id).modelId)
        } finally { scope.cancel() }
    }
    @Test fun historySurvivesPollAndQueueDoesNotCrossControllerChange() = runBlocking {
        var control = 1L
        var state = "running"
        val actions = mutableListOf<String>()
        fun message(key: String) = JSONObject().put("id", key).put("role", "assistant").put("text", key)
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val action = JSONObject(body).getString("action"); actions += action
                val value = session(state, control).put("messages", JSONArray().put(message("latest")))
                return when (action) {
                    "catalog" -> JSONObject().put("hostId", "host").put("agents", JSONArray()).put("sessions", JSONArray().put(value))
                    "history" -> JSONObject().put("hostId", "host").put("history", JSONObject().put("before", 0).put("messages", JSONArray().put(message("older"))))
                    else -> JSONObject().put("hostId", "host").put("session", value)
                }.toString()
            }
        }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(repo, scope, prefs())
            workspace.refresh(pc); workspace.refresh(pc, id)
            workspace.loadHistory(workspace.conversations.getValue(id)); workspace.refresh(pc, id)
            assertEquals(listOf("older", "latest"), workspace.conversations.getValue(id).messages.map { it.id })
            workspace.drafts[id] = "next task"; workspace.enqueue(workspace.conversations.getValue(id))
            control = 2; state = "idle"; workspace.refresh(pc, id)
            assertFalse(actions.contains("send")); assertEquals("next task", workspace.queued.getValue(id).single().text)
            workspace.closeView(id); assertTrue(workspace.queued[id].isNullOrEmpty()); assertFalse(actions.contains("end"))
        } finally { scope.cancel() }
    }
    @Test fun oldHostHidesControlsAndUnreportedUsageIsNotInvented() {
        val old = parseConversation(pc, session().apply { remove("capabilities"); remove("settingsVersion") })
        assertFalse(old.capabilities.configure); assertFalse(old.capabilities.attachments)
        assertEquals("Agent 暂未报告用量", aiUsageLabel("{}"))
        assertTrue(aiUsageLabel("""{"tokens":{"input_tokens":4,"output_tokens":2},"costUsd":0.12}""").contains("0.12"))
    }
    @Test fun backgroundMissingOtherSessionDoesNotEraseSelectedConversation() = runBlocking {
        val missing = UUID.randomUUID().toString()
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String = if (JSONObject(body).optString("action") == "catalog")
                JSONObject().put("hostId", "host").put("agents", JSONArray()).put("sessions", JSONArray().put(session())).toString()
            else """{"hostId":"host","error":"SESSION_NOT_FOUND"}"""
        }
        val workspace = AiWorkspace(repo, this, prefs())
        workspace.refresh(pc); workspace.selected = id
        workspace.refresh(pc, missing)
        assertTrue(workspace.conversations.containsKey(id))
        workspace.refresh(pc, id)
        assertFalse(workspace.conversations.containsKey(id))
        workspace.retire()
    }
    private fun prefs() = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-features-" + UUID.randomUUID(), Context.MODE_PRIVATE)
}

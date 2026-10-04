package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiModelTest {
    @Test fun modelsAreReadOnlyAndBoundToDeviceAgentAndHost(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val other = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-model-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var host = "host-1"
        var listing = true
        var strategy = "native"
        var delayed: CompletableDeferred<String>? = null
        var error = ""
        var malformed = false
        val requests = mutableListOf<Pair<UUID, JSONObject>>()
        fun page(agent: String = "codex") = JSONObject().put("hostId", host).put("agentId", agent)
            .put("models", org.json.JSONArray(if (malformed) """[{"modelId":"bad\nmodel"}]""" else """[
                {"modelId":"model-a","name":"Model A","isDefault":true},
                {"modelId":"model-a","name":"Duplicate"}, {"modelId":"custom-model","name":"Custom"}]
            """)).put("truncated", true).put("error", error).toString()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body); requests += pcId to request
                return if (request.getString("action") == "catalog") """{"hostId":"$host","modelListing":$listing,
                    "agents":[{"id":"codex","name":"Codex","status":"available","strategy":"$strategy"},
                        {"id":"claude","name":"Claude","status":"available"}],"sessions":[]}"""
                else delayed?.await() ?: page(request.getString("agentId"))
            }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc); workspace.refresh(other)
            val models = workspace.models(pc, "codex")
            assertEquals(listOf("model-a", "custom-model"), models.models.map { it.modelId })
            assertTrue(models.truncated); assertTrue(models.models.first().isDefault)
            assertFalse(workspace.busy); assertFalse(workspace.uncertain); assertFalse(prefs.contains("pending"))
            assertEquals(pc, requests.last().first)
            assertFalse(requests.last().second.has("operationId"))
            workspace.models(other, "claude")
            assertEquals(other, requests.last().first)
            assertEquals("claude", requests.last().second.getString("agentId"))
            malformed = true
            assertTrue(runCatching { workspace.models(pc, "codex") }.exceptionOrNull() is IllegalArgumentException)
            malformed = false; error = "MODEL_LIST_UNAVAILABLE"
            assertTrue(runCatching { workspace.models(pc, "codex") }.exceptionOrNull() is java.io.IOException)
            assertFalse(workspace.uncertain); error = ""
            delayed = CompletableDeferred(page("claude"))
            assertTrue(runCatching { workspace.models(pc, "codex") }.isFailure)
            val old = page()
            delayed = CompletableDeferred()
            val pending = async { runCatching { workspace.models(pc, "codex") } }; yield()
            host = "host-2"; workspace.refresh(pc); delayed!!.complete(old)
            assertTrue(pending.await().isFailure)
            delayed = null; listing = false; workspace.refresh(pc)
            val before = requests.size
            assertTrue(runCatching { workspace.models(pc, "codex") }.isFailure)
            assertEquals(before, requests.size)
            listing = true; strategy = "desktop_remote"; workspace.refresh(pc)
            assertTrue(runCatching { workspace.models(pc, "codex") }.isFailure)
            assertEquals(before + 1, requests.size)
            workspace.retire()
            assertTrue(runCatching { workspace.models(other, "claude") }.isFailure)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }
}

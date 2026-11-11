package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.UUID

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiStrategyTest {
    @Test fun onlyNativeAgentsAreSupportedAndLegacyCatalogCannotStartNativeOperations(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val sessionId = UUID.randomUUID().toString()
        val requests = mutableListOf<JSONObject>()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-strategy-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var zcodeStrategy = "desktop_remote"
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body); requests += request
                return when (request.getString("action")) {
                    "catalog" -> """{
                        "hostId":"host", "defaultDirectory":"D:\\projects", "sessions":[], "agents":[
                        {"id":"codex","name":"Codex","status":"available"},
                        {"id":"claude","name":"Claude","status":"missing"},
                        {"id":"zcode","name":"ZCode","status":"available","strategy":"$zcodeStrategy",
                          "nativeHistory":true,"configuration":{"modelListing":true}},
                        {"id":"future","name":"Future","status":"available","strategy":"unknown"}]}
                    """
                    "create" -> {
                        assertEquals("native", zcodeStrategy)
                        assertEquals("zcode", request.getString("agentId"))
                        assertEquals("D:\\projects", request.getString("directory"))
                        """{"hostId":"host","session":{"kind":"ai","id":"$sessionId","agentId":"zcode","agentName":"ZCode",
                          "directory":"D:\\projects","state":"idle","controlVersion":1,"canControl":true}}"""
                    }
                    else -> error("Unexpected non-native request: " + request.getString("action"))
                }
            }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc)
            val catalog = workspace.catalogs.getValue(pc)
            assertEquals(listOf("codex"), catalog.agents.filter { it.supported }.map { it.id })
            assertEquals("native", catalog.agents.first().strategy)
            assertEquals("D:\\projects", workspace.preferredDirectory(pc))
            assertTrue(aiCatalogEntries(pc, catalog).isEmpty())
            assertTrue(catalog.requiresNativeUpgrade())
            assertFalse(catalog.copy(agents = catalog.agents + AiAgentOption("zcode", "ZCode", "", "available")).requiresNativeUpgrade())
            assertTrue(runCatching { workspace.models(pc, "zcode") }.isFailure)
            assertTrue(runCatching { workspace.nativeHistory(pc, "zcode") }.isFailure)
            assertNull(workspace.mutate(pc, "create", extra = JSONObject().put("agentId", "zcode")))
            assertNull(workspace.mutate(pc, "import-native", extra = JSONObject().put("agentId", "zcode")))
            assertTrue(workspace.error.contains("升级电脑端"))
            assertFalse(prefs.contains("pending"))
            assertEquals(listOf("catalog"), requests.map { it.getString("action") })

            zcodeStrategy = "native"
            workspace.refresh(pc)
            assertEquals(listOf("codex", "zcode"), workspace.catalogs.getValue(pc).agents.filter { it.supported }.map { it.id })
            assertTrue(workspace.mutate(pc, "create", extra = JSONObject().put("agentId", "zcode")
                .put("directory", "D:\\projects").put("modelId", "model").put("providerId", "provider"))!!.await())
            assertEquals(sessionId, workspace.selected)
            assertEquals(AiOpened(pc, sessionId, agentId = "zcode"), workspace.opened.single())
            assertEquals("zcode", workspace.conversations.getValue(sessionId).agentId)
            assertFalse(requests.any { it.getString("action") == "remote" })
            val nativeChat = workspace.conversations.getValue(sessionId).let { it.copy(capabilities = it.capabilities.copy(watch = true)) }
            workspace.conversations[sessionId] = nativeChat
            workspace.drafts[sessionId] = "keep this draft"
            zcodeStrategy = "desktop_remote"
            workspace.catalogs[pc] = catalog.copy(sessions = listOf(nativeChat))
            val before = requests.size
            assertTrue(runCatching { workspace.readFeature(nativeChat, "artifacts") }.isFailure)
            assertTrue(runCatching { workspace.renewControl(nativeChat) }.isFailure)
            assertNull(workspace.send(nativeChat))
            workspace.refresh(pc, sessionId)
            assertEquals(before, requests.size)
            assertEquals(nativeChat, workspace.conversations[sessionId])
            assertEquals("keep this draft", workspace.drafts[sessionId])
            workspace.retire(clearPending = true)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun legacyRemoteViewsAreRemovedWithoutDroppingNativeChatsOrDrafts() {
        val pc = UUID.randomUUID()
        val first = UUID.randomUUID().toString(); val second = UUID.randomUUID().toString()
        val remote = "remote:$pc:zcode"
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-migrate-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        fun stored(id: String, strategy: String? = null) = JSONObject().put("pc", pc).put("session", id)
            .put("agentId", "zcode").apply { if (strategy != null) put("strategy", strategy) }
        prefs.edit().putString("opened", JSONArray().put(stored(first)).put(stored(remote, "desktop_remote"))
            .put(JSONObject().put("broken", true)).put(stored(second, "native"))
            .put(stored(UUID.randomUUID().toString(), "unknown"))
            .put(stored(UUID.randomUUID().toString()).put("strategy", JSONObject.NULL)).toString())
            .putString("drafts", JSONObject().put("drafts", JSONObject().put(first, "preserve first")
                .put(second, "preserve second").put(remote, "old remote draft")).toString()).commit()
        var requests = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String { requests++; error("Migration must be local") }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            assertEquals(listOf(first, second), workspace.opened.map { it.sessionId })
            assertEquals(mapOf(first to "preserve first", second to "preserve second"), workspace.drafts.toMap())
            assertFalse(prefs.getString("opened", "")!!.contains("desktop_remote"))
            assertFalse(prefs.getString("opened", "")!!.contains(remote))
            workspace.selected = remote
            assertNull(workspace.selected)
            workspace.selected = first
            assertEquals(first, workspace.selected)
            assertEquals(0, requests)
            workspace.retire()
            val restored = AiWorkspace(repository, scope, prefs)
            assertEquals(listOf(first, second), restored.opened.map { it.sessionId })
            assertEquals("preserve first", restored.drafts[first]); assertEquals("preserve second", restored.drafts[second])
            assertEquals(0, requests)
            restored.retire()
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }
}

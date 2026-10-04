package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
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
class AiHistoryListTest {
    @Test fun filteringUsesDeviceAgentAndActualTitleAndRetainsOfflineAgentIdentity() {
        val pc = UUID.randomUUID(); val second = UUID.randomUUID(); val id = UUID.randomUUID().toString()
        val session = parseConversation(pc, JSONObject("""{"kind":"ai","id":"$id","agentId":"codex","agentName":"Codex","directory":"D:\\work","state":"idle","controlVersion":1,"canControl":true,"title":"修复登录","preview":"摘要"}"""))
        val entries = listOf(AiOpened(pc, id, agentId = "codex"), AiOpened(second, "remote", "desktop_remote", "zcode"))
        val sessions = mapOf(id to session); val devices = mapOf(pc to "HOME-PC", second to "OTHER-PC")
        assertEquals(listOf(entries[0]), aiVisibleEntries(entries, sessions, devices, "登录", false))
        assertEquals(listOf(entries[0]), aiVisibleEntries(entries, sessions, devices, "home", false))
        assertEquals(listOf(entries[1]), aiVisibleEntries(entries, sessions, devices, "zcode", false))
        assertEquals("Codex", aiEntryAgent(entries[0], null))
        assertTrue(aiVisibleEntries(entries, sessions, devices, "", true).isEmpty())
    }

    @Test fun nativeHistoryIsReadOnlyFencedAndOnlyImportedSessionOpens(): Unit = runBlocking {
        val pc = UUID.randomUUID(); val nativeId = UUID.randomUUID().toString(); val sessionId = UUID.randomUUID().toString()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-history-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var host = "host1"; var delay: CompletableDeferred<String>? = null
        val requests = mutableListOf<JSONObject>()
        fun page() = """{"hostId":"$host","agentId":"codex","nativeHistory":{"sessions":[{"nativeId":"$nativeId","title":"原生标题","directory":"D:\\work","updatedAtMs":123}],"nextCursor":"next","truncated":false}}"""
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body); requests += request
                return when (request.getString("action")) {
                    "catalog" -> """{"hostId":"$host","agents":[{"id":"codex","name":"Codex","status":"available","nativeHistory":true}],"sessions":[]}"""
                    "native-history" -> delay?.await() ?: page()
                    else -> """{"hostId":"$host","session":{"kind":"ai","id":"$sessionId","agentId":"codex","agentName":"Codex","directory":"D:\\work","state":"idle","controlVersion":1,"canControl":true,"permissionMode":"historical","title":"原生标题"}}"""
                }
            }
        }
        try {
            val workspace = AiWorkspace(repo, scope, prefs); workspace.refresh(pc)
            assertEquals("原生标题", workspace.nativeHistory(pc, "codex").sessions.single().title)
            assertTrue(workspace.opened.isEmpty()); assertFalse(prefs.contains("pending")); assertFalse(requests.last().has("operationId"))
            val old = page(); delay = CompletableDeferred()
            val pending = async { runCatching { workspace.nativeHistory(pc, "codex") } }; yield()
            host = "host2"; workspace.refresh(pc); delay!!.complete(old)
            assertTrue(pending.await().isFailure); delay = null
            workspace.mutate(pc, "import-native", extra = JSONObject().put("agentId", "codex").put("requestId", nativeId))
            assertEquals(sessionId, workspace.selected); assertEquals("historical", workspace.conversations[sessionId]?.permissionMode)
            val restored = AiWorkspace(repo, scope, prefs)
            assertEquals("codex", restored.opened.single().agentId)
            assertTrue(restored.conversations.isEmpty()); assertFalse(prefs.getString("opened", "")!!.contains("原生标题"))
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }
}

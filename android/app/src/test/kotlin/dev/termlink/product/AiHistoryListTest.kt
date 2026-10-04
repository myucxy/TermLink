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
    @Test fun deviceGroupsLocateCorrectPcAndKeepCatalogBrowsingSeparateFromOpenedViews() {
        val pc = UUID.randomUUID(); val other = UUID.randomUUID()
        fun session(device: UUID, agent: String, name: String = agent) = parseConversation(device, JSONObject()
            .put("id", UUID.randomUUID().toString()).put("kind", "ai").put("agentId", agent).put("agentName", name)
            .put("directory", "D:\\work").put("state", "idle").put("controlVersion", 1).put("canControl", true))
        val first = session(other, "codex", "Codex")
        val target = session(pc, "codex", "Codex")
        val claude = session(pc, "", "Claude Code")
        val sessions = listOf(first, target, claude).associateBy { it.id }
        val catalog = AiCatalog("host", listOf(AiAgentOption("zcode", "ZCode", "", "available", "desktop_remote")), listOf(target, claude))
        val opened = listOf(AiOpened(other, first.id, agentId = "codex"), AiOpened(pc, target.id, agentId = "codex"))
        val entries = aiListEntries(opened, pc, catalog, true)
        assertEquals(4, entries.size) // One duplicate plus one desktop entry, not duplicated native views.
        assertEquals(opened, aiListEntries(opened, pc, catalog, false)) // Offline: no cached catalog disclosures.
        assertEquals(opened, aiListEntries(opened, null, catalog, true))
        assertEquals(listOf("codex", "claude", "zcode"), aiCatalogEntries(pc, catalog).map { aiEntryAgentId(it, sessions[it.sessionId]) })
        val rows = aiListRows(entries, sessions, emptyMap())
        val targetKey = AiGroupTarget(pc, "codex").key
        val index = rows.indexOfFirst { it.key == targetKey }
        assertTrue(index > rows.indexOfFirst { it.key == AiGroupTarget(other, "codex").key })
        assertEquals(AiListRow.Session(entries.first { it.sessionId == target.id }), rows[index + 1])
        assertEquals(rows.size, rows.map { it.key }.distinct().size)
        assertFalse(aiListRows(entries, sessions, mapOf("device:$pc" to true)).any { it.key == targetKey })
        val closedGroup = aiListRows(entries, sessions, mapOf(targetKey to true))
        assertTrue(closedGroup.any { it.key == targetKey })
        assertFalse(closedGroup.any { it is AiListRow.Session && it.entry.sessionId == target.id })
        assertEquals(2, opened.size)
    }

    @Test fun groupNavigationClearsOldConversationAndFiltersWithoutOpeningAnySession() {
        val pc = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-group-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val workspace = AiWorkspace(UnavailableProductRepository(), scope, prefs)
            workspace.selected = UUID.randomUUID().toString()
            workspace.beginCreate(UUID.randomUUID())
            workspace.search = "old filter"; workspace.attentionOnly = true
            workspace.browseAgentGroup(pc, "Claude Code")
            assertNull(workspace.selected); assertNull(workspace.createPc)
            assertEquals("", workspace.search); assertFalse(workspace.attentionOnly)
            assertEquals(pc, workspace.listPc)
            assertEquals(AiGroupTarget(pc, "claude"), workspace.listTarget)
            assertTrue(workspace.opened.isEmpty()); assertFalse(prefs.contains("opened")); assertFalse(prefs.contains("pending"))
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

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

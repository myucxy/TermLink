package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiWebConversationTest {
    private val pc = UUID.randomUUID()
    private val id = UUID.randomUUID().toString()
    private fun session() = AiConversation(id, pc, "Codex", "D:\\work", "idle", "", 1, true,
        listOf(AiMessage("one", "assistant", "hello")), emptyList(), false)

    @Test fun bridgeAllowlistAndArtifactScope() {
        assertEquals("search", aiWebRequest("""{"id":"1","action":"search"}""").getString("action"))
        for (action in listOf("exec", "attachment.chunk", "configure", "openFile", "javascript")) {
            assertThrows(IllegalArgumentException::class.java) { aiWebRequest("""{"id":"2","action":"$action"}""") }
        }
        assertEquals("src/main.ts", aiArtifactRelativePath("D:\\work", "D:\\work\\src\\main.ts"))
        for (path in listOf("../secret", "D:\\workspace\\file", "\\\\server\\file", "/etc/passwd", "safe.txt:secret")) {
            assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("D:\\work", path) }
        }
    }

    @Test fun uiStateOnlyRestoresCurrentQuestionsAndCliUsesDistinctWorkspace() {
        val q = AiQuestion("q", "范围", listOf("测试", "全部"), false)
        val p = AiPending("request", "question", "", listOf(q), true)
        val current = session().copy(pending = listOf(p))
        val draft = org.json.JSONObject().put("schema", aiQuestionSchema(p)).put("answers", org.json.JSONObject().put("q", org.json.JSONArray(listOf("未知", "测试", "全部"))))
            .put("extra", org.json.JSONObject().put("q", "保留 CLI"))
        val state = org.json.JSONObject().put("questions", org.json.JSONObject().put(p.id, draft).put("old", draft))
        val safe = sanitizeAiWebState(state, current).getJSONObject("questions")
        assertEquals(1, safe.length()); assertEquals("测试", safe.getJSONObject(p.id).getJSONObject("answers").getJSONArray("q").getString(0))
        assertEquals(0, sanitizeAiWebState(state, session()).getJSONObject("questions").length())
        assertEquals(0, sanitizeAiWebState(state, current.copy(pending = listOf(p.copy(questions = listOf(q.copy(text = "不同请求")))))).getJSONObject("questions").length())
        val path = cliWorkspacePath("D:\\TermLink\\AiWorkspaces", java.util.Date(0), UUID.fromString("11111111-1111-1111-1111-111111111111"))
        assertTrue(path.matches(Regex("D:\\\\TermLink\\\\AiWorkspaces\\\\cli_[0-9]{8}_[0-9]{6}_[0-9]{3}_11111111111111111111111111111111")))
    }

    @Test fun unchangedMessagesAreNotResentAndHistoryPrependsHaveExplicitOrder() {
        val updates = AiWebUpdates(); val session = session()
        val first = updates.encode(session, false, false, false, 1f)
        assertTrue(first.getBoolean("reset")); assertEquals(1, first.getJSONArray("messages").length())
        val idle = updates.encode(session.copy(state = "running"), false, false, false, 1f)
        assertEquals(0, idle.getJSONArray("messages").length()); assertFalse(idle.has("order"))
        val older = AiMessage("old", "user", "question")
        val update = updates.encode(session.copy(messages = listOf(older) + session.messages), false, false, false, 1f)
        assertEquals("old", update.getJSONArray("order").getString(0)); assertEquals(1, update.getJSONArray("messages").length())
        val reset = updates.encode(session.copy(messages = emptyList()), false, false, false, 1f)
        assertEquals(0, reset.getJSONArray("order").length())
    }

    @Test fun restoredDraftQueueDoesNotAutomaticallySendAndLogoutClearsIt(): Unit = runBlocking {
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-web-$id", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var requests = 0
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                requests++; return """{"hostId":"host","agents":[],"sessions":[]}"""
            }
        }
        try {
            val workspace = AiWorkspace(repo, scope, prefs, seal = { "protected:" + it }, unseal = { it.removePrefix("protected:") })
            workspace.catalogs[pc] = AiCatalog("host", emptyList(), emptyList())
            workspace.open(session()); workspace.drafts[id] = "unsent"
            workspace.selected = id
            workspace.saveWebUiState(session(), org.json.JSONObject().put("tail", false).put("anchor", org.json.JSONObject().put("id", "one").put("offset", -20)))
            assertTrue(prefs.getString("web:$id", "")!!.startsWith("protected:"))
            workspace.queued[id] = listOf(AiQueued(UUID.randomUUID().toString(), "queued", "host", 1))
            workspace.fileReferences[id] = listOf("D:\\work\\test.txt")
            workspace.retire()
            assertTrue(prefs.getString("drafts", "")!!.startsWith("protected:"))
            val restored = AiWorkspace(repo, scope, prefs, unseal = { it.removePrefix("protected:") })
            assertEquals("unsent", restored.drafts[id]); assertTrue(restored.queued.getValue(id).single().restored)
            restored.catalogs[pc] = AiCatalog("host", emptyList(), emptyList())
            assertEquals("one", restored.webUiState(session()).getJSONObject("anchor").getString("id"))
            restored.catalogs[pc] = AiCatalog("another-host", emptyList(), emptyList())
            assertEquals(0, restored.webUiState(session()).length())
            assertEquals(0, requests)
            restored.retire(clearPending = true); assertFalse(prefs.contains("drafts"))
            assertFalse(prefs.contains("web:$id"))
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun attachmentRecoveryRequiresValidationAndLogoutDeletesCache(): Unit = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-upload-$id", Context.MODE_PRIVATE)
        val dir = java.io.File(context.cacheDir, "ai-test-$id").also { it.mkdirs() }
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        try {
            val key = UUID.randomUUID().toString(); val file = java.io.File(dir, key).also { it.writeText("abc") }
            val workspace = AiWorkspace(UnavailableProductRepository(), scope, prefs, uploadDirectory = dir)
            workspace.open(session())
            workspace.attachments[id] = listOf(AiUpload(key, android.net.Uri.EMPTY, "host", 1).apply {
                name = "notes.txt"; size = 3; hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"; this.file = file; status = "ready"
            })
            workspace.retire(); assertTrue(file.isFile)
            val restored = AiWorkspace(UnavailableProductRepository(), scope, prefs, uploadDirectory = dir)
            val upload = restored.attachments.getValue(id).single()
            assertEquals("failed", upload.status); assertTrue(upload.error.contains("重试"))
            upload.validateCache(AiCapabilities(maxFileBytes = 1024))
            file.writeText("bad")
            try { upload.validateCache(AiCapabilities(maxFileBytes = 1024)); fail("Changed attachment accepted") } catch (_: IllegalArgumentException) { }
            restored.retire(clearPending = true); assertFalse(file.exists())
        } finally { scope.cancel(); prefs.edit().clear().commit(); dir.listFiles()?.forEach { it.delete() }; dir.delete() }
    }

    @Test fun notificationApprovalRequiresFreshMatchingAuthority() {
        val active = session().copy(pending = listOf(AiPending("approval", "command", "test", emptyList(), true)))
        val ticket = JSONObject().put("pcId", pc).put("sessionId", id).put("hostId", "host")
            .put("controlVersion", 1).put("requestId", "approval").put("decision", "allow").put("expiresAt", System.currentTimeMillis() + 60000)
        assertTrue(aiNotificationCanAnswer(ticket, "host", active))
        assertFalse(aiNotificationCanAnswer(ticket, "restarted", active))
        assertFalse(aiNotificationCanAnswer(ticket, "host", active.copy(controlVersion = 2)))
        assertFalse(aiNotificationCanAnswer(ticket, "host", active.copy(canControl = false)))
        assertFalse(aiNotificationCanAnswer(ticket, "host", active.copy(pending = emptyList())))
        assertFalse(aiNotificationCanAnswer(ticket, "host", active.copy(id = "different")))
        ticket.put("expiresAt", 0); assertFalse(aiNotificationCanAnswer(ticket, "host", active))
    }

    @Test fun rejectedMutationDoesNotReportSuccess(): Unit = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-receipt-$id", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val repo = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String) = """{"hostId":"host","error":"CONTROL_CHANGED"}"""
        }
        val workspace = AiWorkspace(repo, scope, prefs)
        try {
            workspace.catalogs[pc] = AiCatalog("host", emptyList(), emptyList())
            assertEquals(false, workspace.mutate(pc, "answer", session())?.await())
            assertFalse(prefs.contains("pending")); assertFalse(workspace.uncertain)
        } finally { workspace.retire(true); scope.cancel(); prefs.edit().clear().commit() }
    }
}

package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.runBlocking
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
    @Test fun remoteLinksRejectUntrustedOriginsAndActiveSchemes() {
        assertTrue(isAllowedZcodeRemoteUrl("https://zcode.z.ai/remote/v4?pair=fixture#test"))
        assertTrue(isAllowedZcodeRemoteUrl("https://ZCODE.Z.AI:443/remote/v4"))
        listOf("http://zcode.z.ai/remote/v4", "https://zcode.z.ai.evil.test/remote/v4", "javascript:alert(1)",
            "https://user@zcode.z.ai/remote/v4", "https://zcode.z.ai:8443/remote/v4", "https://zcode.z.ai/other",
            "https://zcode.z.ai/remote/../../other", "https://zcode.z.ai/remote/v4\n", "file:///remote/v4",
            "https://evil.test\\@zcode.z.ai/remote/v4", "https://zcode.z.ai/remote/v4?x=" + "a".repeat(8192))
            .forEach { assertFalse(isAllowedZcodeRemoteUrl(it)) }
    }

    @Test fun catalogDefaultsAndRemoteRoutingKeepCredentialsOutOfStoredViews(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val requests = mutableListOf<String>()
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("ai-strategy-$pc", Context.MODE_PRIVATE)
        var remoteUrl = "https://zcode.z.ai/remote/v4?pair=fixture-secret"
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body)
                requests += request.getString("action")
                return if (request.getString("action") == "catalog") """{
                    "hostId":"host", "defaultDirectory":"D:\\projects", "sessions":[], "agents":[
                    {"id":"codex","name":"Codex","status":"available"},
                    {"id":"claude","name":"Claude","status":"missing"},
                    {"id":"zcode","name":"ZCode","status":"available","strategy":"desktop_remote"},
                    {"id":"future","name":"Future","status":"available","strategy":"unknown"}]}
                """ else {
                    assertEquals("remote", request.getString("action"))
                    assertEquals("host", request.getString("hostId"))
                    JSONObject().put("hostId", "host").put("agentId", "zcode").put("strategy", "desktop_remote")
                        .put("remoteUrl", remoteUrl).toString()
                }
            }
        }
        try {
            val workspace = AiWorkspace(repository, this, prefs)
            workspace.refresh(pc)
            assertEquals(listOf("codex", "zcode"), workspace.catalogs.getValue(pc).agents.filter { it.supported }.map { it.id })
            assertEquals("native", workspace.catalogs.getValue(pc).agents.first().strategy)
            assertEquals("D:\\projects", workspace.preferredDirectory(pc))
            workspace.openRemote(pc, "codex")
            assertNull(workspace.selected)
            workspace.openRemote(pc, "zcode")
            workspace.openRemote(pc, "zcode")
            val entry = workspace.opened.single()
            assertEquals(entry.sessionId, workspace.selected)
            assertTrue(workspace.conversations.isEmpty())
            assertEquals(remoteUrl, workspace.remoteAddress(entry))
            assertEquals(listOf("catalog", "remote"), requests)
            assertFalse(prefs.all.toString().contains("fixture-secret"))
            val restored = AiWorkspace(repository, this, prefs)
            assertEquals(entry, restored.opened.single())
            restored.retire()
            remoteUrl = "https://untrusted.test/remote/v4"
            try { workspace.remoteAddress(entry); fail("Untrusted host accepted") } catch (_: IllegalArgumentException) { }
            val beforeClose = requests.size
            workspace.closeView(entry.sessionId)
            assertEquals(beforeClose, requests.size)
            assertTrue(workspace.opened.isEmpty())
            workspace.retire()
        } finally { prefs.edit().clear().commit() }
    }
}

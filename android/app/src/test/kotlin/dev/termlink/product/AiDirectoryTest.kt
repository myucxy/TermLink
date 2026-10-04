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
class AiDirectoryTest {
    @Test fun managedCreatesDoNotReplaceCustomDirectoryPreferenceAndBrowseIsHostBound(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("ai-directory-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var host = "host-1"
        var nextOffset: Long? = null
        var delayed: CompletableDeferred<String>? = null
        val requests = mutableListOf<JSONObject>()
        fun page() = JSONObject().put("hostId", host).put("directories", JSONObject("""{
            "currentPath":"D:\\projects", "parentPath":"D:\\", "entries":[
            {"name":"示例项目", "path":"D:\\projects\\示例项目", "kind":"directory"}]}
        """).put("nextOffset", nextOffset ?: JSONObject.NULL)).toString()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body)
                requests += request
                return when (request.getString("action")) {
                    "catalog" -> """{"hostId":"$host","agents":[],"sessions":[],"defaultDirectory":"D:\\TermLink\\AiWorkspaces","directoryMode":"per_session","directoryBrowsing":true}"""
                    "directories" -> delayed?.await() ?: page()
                    else -> JSONObject().put("hostId", host).put("session", JSONObject()
                        .put("id", UUID.randomUUID().toString()).put("kind", "ai").put("agentName", "Codex")
                        .put("directory", if (request.optString("directoryMode") == "managed") "D:\\TermLink\\AiWorkspaces\\codex_new" else request.getString("directory"))
                        .put("state", "idle").put("canControl", true).put("controlVersion", 1)).toString()
                }
            }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs)
            workspace.refresh(pc)
            assertEquals("per_session", workspace.catalogs.getValue(pc).directoryMode)
            assertTrue(workspace.catalogs.getValue(pc).directoryBrowsing)
            workspace.mutate(pc, "create", extra = JSONObject().put("directoryMode", "managed").put("directory", ""))
            assertNotNull(workspace.selected)
            assertFalse(prefs.contains("directory:$pc"))
            workspace.mutate(pc, "create", extra = JSONObject().put("directoryMode", "custom").put("directory", "D:\\projects"))
            assertEquals("D:\\projects", workspace.preferredDirectory(pc))
            workspace.mutate(pc, "create", extra = JSONObject().put("directoryMode", "managed").put("directory", ""))
            assertEquals("D:\\projects", workspace.preferredDirectory(pc))
            val folders = workspace.directories(pc, "D:\\projects")
            assertEquals("示例项目", folders.entries.single().name)
            assertEquals("D:\\projects", folders.currentPath)
            assertNull(folders.nextOffset)
            assertEquals("host-1", requests.last().getString("hostId"))
            nextOffset = 0
            try { workspace.directories(pc, "D:\\projects"); fail("Non-advancing page accepted") } catch (_: IllegalArgumentException) { }
            nextOffset = null
            val staleResponse = page()
            delayed = CompletableDeferred()
            val reading = async { runCatching { workspace.directories(pc, "D:\\projects") } }
            yield()
            host = "host-2"
            workspace.refresh(pc)
            delayed!!.complete(staleResponse)
            assertTrue(reading.await().exceptionOrNull() is IllegalStateException)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun fencedRepliesPreserveStreamingCodeAndRejectShortOrIndentedClosingFences() {
        val block = aiMarkdown("说明\n```kotlin\nval x = 1\n```\n完成").firstChild.next as org.commonmark.node.FencedCodeBlock
        assertEquals("val x = 1\n", block.literal); assertEquals("kotlin", block.info)
        val streaming = aiMarkdown("```text\na\n``  \n    ```\nb").firstChild as org.commonmark.node.FencedCodeBlock
        assertEquals("a\n``  \n    ```\nb\n", streaming.literal)
        assertEquals("```\ncode\n", (aiMarkdown("~~~\n```\ncode\n~~~~").firstChild as org.commonmark.node.FencedCodeBlock).literal)
        assertTrue(aiMarkdown("    ```\nplain").firstChild is org.commonmark.node.IndentedCodeBlock)
        assertTrue(aiMarkdown("```inline```\n正文").firstChild is org.commonmark.node.Paragraph)
    }
}

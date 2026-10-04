package dev.termlink.product

import android.content.Context
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import java.io.ByteArrayOutputStream
import java.util.UUID
import kotlinx.coroutines.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class AiDownloadsTest {
    @Test fun pinsVersionAndKeepsDownloadIndependentOfTheConversationView() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("downloads-test-${UUID.randomUUID()}", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val pc = UUID.randomUUID(); val gate = CompletableDeferred<Unit>(); val bytes = ByteArrayOutputStream()
        val uri = Uri.parse("content://downloads/test/result.txt")
        shadowOf(context.contentResolver).registerOutputStream(uri, bytes)
        var observed: JSONObject? = null
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                observed = JSONObject(body); gate.await()
                return JSONObject().put("hostId", "h1").put("artifacts", chunk()).toString()
            }
        }
        try {
            val workspace = AiWorkspace(repository, scope, prefs, context = context)
            val session = AiConversation("s1", pc, "Codex", "D:\\Work", "idle", "", 1, true, emptyList(), emptyList(), false)
            workspace.conversations[session.id] = session
            workspace.downloads!!.start(session, "h1", "PC", AiArtifactDownload("f1", "v1", "result.txt"), uri)
            workspace.closeView("s1")
            gate.complete(Unit)
            withTimeout(5000) { while (workspace.downloads.records.values.single().status.isActive) yield() }
            assertEquals(RemoteDownloadStatus.COMPLETED, workspace.downloads.records.values.single().status)
            assertEquals("abc", bytes.toString("UTF-8"))
            assertEquals("h1", observed!!.getString("hostId")); assertEquals("s1", observed!!.getString("sessionId"))
            workspace.retire()
            val restored = AiDownloads(context, repository, scope, prefs, { it }, { it })
            assertEquals(RemoteDownloadStatus.COMPLETED, restored.records.values.single().status)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun rejectsChangedVersionOffsetsAndTruncatedResponses() {
        assertEquals(3L, validateAiArtifactChunk(chunk(), "f1", "v1", 0, null, 3))
        for (bad in listOf(chunk().put("version", "v2"), chunk().put("offset", 1), chunk().put("size", 2), chunk().put("nextOffset", 0))) {
            try { validateAiArtifactChunk(bad, "f1", "v1", 0, null, 3); fail("Invalid artifact accepted") }
            catch (_: IllegalArgumentException) { }
        }
    }
    @Test fun requestTimeoutIsFailedRatherThanUserCancellation() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("downloads-timeout-${UUID.randomUUID()}", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String = withTimeout(1) { awaitCancellation() }
        }
        try {
            val downloads = AiDownloads(context, repository, scope, prefs, { it }, { it })
            downloads.start(AiConversation("s", UUID.randomUUID(), "Codex", "D:\\Work", "idle", "", 1, true, emptyList(), emptyList(), false),
                "host", "PC", AiArtifactDownload("file", "v1", "result.txt"), Uri.parse("content://downloads/test/timeout"))
            withTimeout(5_000) { while (downloads.records.values.single().status.isActive) yield() }
            assertEquals(RemoteDownloadStatus.FAILED, downloads.records.values.single().status)
            assertTrue(downloads.records.values.single().error!!.contains("超时"))
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }
    private fun chunk() = JSONObject().put("id", "f1").put("version", "v1").put("offset", 0)
        .put("size", 3).put("nextOffset", 3).put("data", "YWJj")
}

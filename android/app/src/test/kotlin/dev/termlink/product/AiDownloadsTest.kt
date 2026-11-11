package dev.termlink.product

import android.content.Context
import android.net.Uri
import androidx.test.core.app.ApplicationProvider
import java.io.ByteArrayOutputStream
import java.util.UUID
import kotlinx.coroutines.*
import org.json.JSONArray
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
        var observedPc: UUID? = null
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                observedPc = pcId; observed = JSONObject(body); gate.await()
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
            assertEquals(pc, observedPc)
            assertEquals("h1", observed!!.getString("hostId")); assertEquals("s1", observed!!.getString("sessionId"))
            assertEquals("f1", observed!!.getString("requestId")); assertEquals("v1", observed!!.getString("cursor"))
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
    @Test fun normalizesHostPathsBeforeDownloadAndPreservesThePinnedOwnerOnRetry() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        data class Case(val root: String, val path: String, val relative: String, val style: String, val platform: String)
        val cases = listOf(
            Case("/srv/项目 Work", "/srv/项目 Work/输出 目录/结果.txt", "输出 目录/结果.txt", "posix", "linux"),
            Case("/srv/项目 Work", "/srv/项目 Work/输出 目录/结果.txt", "输出 目录/结果.txt", "posix", "macos"),
            Case("/", "/输出/结果.txt", "输出/结果.txt", "posix", "unknown"),
            Case("/Users/me/Work", "输出\\草稿/结果\\1.txt", "输出\\草稿/结果\\1.txt", "posix", "macos"),
            Case("D:\\项目 Work", "d:/项目 work/输出 目录/结果.txt", "输出 目录/结果.txt", "windows", "windows"),
            Case("D:\\", "D:\\结果.txt", "结果.txt", "windows", "unknown"),
            Case("\\\\Host\\Share", "//host/share/输出/结果.txt", "输出/结果.txt", "windows", "windows"),
            Case("/srv/work", "result.txt", "result.txt", "", ""),
            Case("D:\\Work", "result.txt", "result.txt", "", ""))
        for ((root, path, relative, style, platform) in cases) {
            val prefs = context.getSharedPreferences("downloads-path-${UUID.randomUUID()}", Context.MODE_PRIVATE)
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
            val pc = UUID.randomUUID(); val sessionId = UUID.randomUUID().toString()
            val observed = mutableListOf<Pair<UUID, JSONObject>>()
            val uri = Uri.parse("content://downloads/test/${UUID.randomUUID()}")
            val bytes = ByteArrayOutputStream()
            shadowOf(context.contentResolver).registerOutputStream(uri, bytes)
            val repository = object : ProductRepository by UnavailableProductRepository() {
                override suspend fun aiRequest(pcId: UUID, body: String): String {
                    observed += pcId to JSONObject(body)
                    if (observed.size == 1) error("Offline")
                    return JSONObject().put("hostId", "original-host").put("artifacts", chunk().put("id", relative)).toString()
                }
            }
            try {
                val session = AiConversation(sessionId, pc, "OpenCode", root, "idle", "", 1, true, emptyList(), emptyList(), false,
                    capabilities = AiCapabilities(pathStyle = style, hostPlatform = platform))
                val downloads = AiDownloads(context, repository, scope, prefs, { it }, { it })
                downloads.start(session, "original-host", "PC", AiArtifactDownload(path, "v1", "结果.txt"), uri)
                withTimeout(5000) { while (downloads.records.values.single().status.isActive) yield() }
                assertEquals(RemoteDownloadStatus.FAILED, downloads.records.values.single().status)
                downloads.retire(false)
                val restored = AiDownloads(context, repository, scope, prefs, { it }, { it }, currentSession = { targetPc, host, targetSession ->
                    assertEquals(pc, targetPc); assertEquals("original-host", host); assertEquals(sessionId, targetSession)
                    session
                })
                restored.retry(restored.records.keys.single(), uri)
                withTimeout(5000) { while (restored.records.values.single().status.isActive) yield() }
                assertEquals(RemoteDownloadStatus.COMPLETED, restored.records.values.single().status)
                assertEquals("abc", bytes.toString("UTF-8")); assertEquals(2, observed.size)
                for ((targetPc, request) in observed) {
                    assertEquals(pc, targetPc); assertEquals(sessionId, request.getString("sessionId"))
                    assertEquals("original-host", request.getString("hostId")); assertEquals(relative, request.getString("requestId"))
                    assertEquals("v1", request.getString("cursor")); assertEquals(0, request.getInt("offset"))
                }
                restored.retire(false)
            } finally { scope.cancel(); prefs.edit().clear().commit() }
        }
    }

    @Test fun rejectsForeignAndTraversingDownloadsBeforeAnyRequestOrDestinationWrite() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("downloads-reject-${UUID.randomUUID()}", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var requests = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String { requests++; error("Unexpected download") }
        }
        val bytes = ByteArrayOutputStream().apply { write("existing".toByteArray()) }
        val uri = Uri.parse("content://downloads/test/reject-${UUID.randomUUID()}")
        shadowOf(context.contentResolver).registerOutputStream(uri, bytes)
        try {
            val downloads = AiDownloads(context, repository, scope, prefs, { it }, { it })
            val session = AiConversation("original-session", UUID.randomUUID(), "Claude Code", "/Users/me/Work", "idle", "", 1, true, emptyList(), emptyList(), false)
            for (path in listOf("../secret", "/Users/me/work/secret", "/etc/passwd", "C:/secret", "\\\\host\\share\\secret"))
                assertThrows(IllegalArgumentException::class.java) { downloads.start(session, "host", "PC", AiArtifactDownload(path, "v1", "result.txt"), uri) }
            for (path in listOf("safe.txt:stream", "D:secret", "D:\\other\\file", "out\\..\\secret", "\\\\?\\D:\\Work\\file"))
                assertThrows(IllegalArgumentException::class.java) { downloads.start(session.copy(directory = "D:\\Work"), "host", "PC", AiArtifactDownload(path, "v1", "result.txt"), uri) }
            for ((root, style) in listOf("/srv/work" to "windows", "D:\\Work" to "posix", "/srv/work" to "unsupported", "D:\\Work" to "plan9")) {
                val active = session.copy(directory = root, capabilities = AiCapabilities(pathStyle = style, hostPlatform = "unknown"))
                assertThrows(IllegalArgumentException::class.java) {
                    downloads.start(active, "host", "PC", AiArtifactDownload("result.txt", "v1", "result.txt"), uri)
                }
            }
            assertEquals(0, requests); assertTrue(downloads.records.isEmpty()); assertFalse(prefs.contains("downloads"))
            assertEquals("existing", bytes.toString("UTF-8"))
            downloads.retire(false)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun retriesValidateCurrentHostRulesAndNeverRebindTheSavedSource() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("downloads-resume-rules-${UUID.randomUUID()}", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val pc = UUID.randomUUID(); val uri = Uri.parse("content://downloads/test/${UUID.randomUUID()}")
        val bytes = ByteArrayOutputStream().apply { write("existing".toByteArray()) }
        shadowOf(context.contentResolver).registerOutputStream(uri, bytes)
        var requests = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String { requests++; error("Offline") }
        }
        val original = AiConversation("saved-session", pc, "OpenCode", "/srv/work", "idle", "", 1, true, emptyList(), emptyList(), false,
            capabilities = AiCapabilities(pathStyle = "posix", hostPlatform = "linux"))
        var current: AiConversation? = original
        var host = "saved-host"
        try {
            val downloads = AiDownloads(context, repository, scope, prefs, { it }, { it })
            downloads.start(original, host, "PC", AiArtifactDownload("result.txt", "v1", "result.txt"), uri)
            withTimeout(5000) { while (downloads.records.values.single().status.isActive) yield() }
            downloads.retire(false)
            val restored = AiDownloads(context, repository, scope, prefs, { it }, { it }, currentSession = { targetPc, targetHost, targetSession ->
                assertEquals(pc, targetPc); assertEquals("saved-host", targetHost); assertEquals(original.id, targetSession)
                current.takeIf { targetHost == host }
            })
            val id = restored.records.keys.single()
            for (candidate in listOf(null, original.copy(pcId = UUID.randomUUID()), original.copy(id = "selected-other-session"),
                original.copy(directory = "/srv/another-workspace"),
                original.copy(capabilities = AiCapabilities(pathStyle = "windows")),
                original.copy(capabilities = AiCapabilities(pathStyle = "unsupported", hostPlatform = "unknown")),
                original.copy(capabilities = AiCapabilities(pathStyle = "plan9")))) {
                current = candidate
                assertThrows(Exception::class.java) { restored.retry(id, uri) }
            }
            current = original; host = "replacement-host"
            assertThrows(IllegalStateException::class.java) { restored.retry(id, uri) }
            assertEquals(1, requests); assertEquals("existing", bytes.toString("UTF-8"))
            assertEquals(RemoteDownloadStatus.FAILED, restored.records.getValue(id).status)
            restored.retire(false)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun legacyDownloadWithoutPathMetadataUsesCurrentSessionCapabilities() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val prefs = context.getSharedPreferences("downloads-legacy-rules-${UUID.randomUUID()}", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val pc = UUID.randomUUID(); val uri = Uri.parse("content://downloads/test/${UUID.randomUUID()}")
        val bytes = ByteArrayOutputStream()
        shadowOf(context.contentResolver).registerOutputStream(uri, bytes)
        val file = "输出\\草稿/结果.txt"
        var requests = 0
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                requests++
                val request = JSONObject(body)
                assertEquals(pc, pcId); assertEquals("legacy-host", request.getString("hostId"))
                assertEquals("legacy-session", request.getString("sessionId")); assertEquals(file, request.getString("requestId"))
                assertEquals("v1", request.getString("cursor"))
                if (requests == 1) error("Offline")
                return JSONObject().put("hostId", "legacy-host").put("artifacts", chunk().put("id", file)).toString()
            }
        }
        val original = AiConversation("legacy-session", pc, "Claude Code", "/srv/work", "idle", "", 1, true, emptyList(), emptyList(), false)
        var current = original.copy(capabilities = AiCapabilities(pathStyle = "unsupported", hostPlatform = "unknown"))
        try {
            val downloads = AiDownloads(context, repository, scope, prefs, { it }, { it })
            downloads.start(original, "legacy-host", "PC", AiArtifactDownload(file, "v1", "结果.txt"), uri)
            withTimeout(5000) { while (downloads.records.values.single().status.isActive) yield() }
            downloads.retire(false)
            val stored = JSONArray(prefs.getString("downloads", null))
            stored.getJSONObject(0).remove("directory"); stored.getJSONObject(0).remove("pathStyle")
            prefs.edit().putString("downloads", stored.toString()).commit()
            val restored = AiDownloads(context, repository, scope, prefs, { it }, { it }, currentSession = { targetPc, host, session ->
                assertEquals(pc, targetPc); assertEquals("legacy-host", host); assertEquals(original.id, session)
                current
            })
            val id = restored.records.keys.single()
            assertThrows(IllegalArgumentException::class.java) { restored.retry(id, uri) }
            assertEquals(1, requests); assertEquals(0, bytes.size())
            current = original.copy(capabilities = AiCapabilities(pathStyle = "posix", hostPlatform = "linux"))
            restored.retry(id, uri)
            withTimeout(5000) { while (restored.records.getValue(id).status.isActive) yield() }
            assertEquals(RemoteDownloadStatus.COMPLETED, restored.records.getValue(id).status)
            assertEquals(2, requests); assertEquals("abc", bytes.toString("UTF-8"))
            restored.retire(false)
        } finally { scope.cancel(); prefs.edit().clear().commit() }
    }

    private fun chunk() = JSONObject().put("id", "f1").put("version", "v1").put("offset", 0)
        .put("size", 3).put("nextOffset", 3).put("data", "YWJj")
}

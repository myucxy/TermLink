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
        for (action in listOf("open-directory", "back-state", "agent.features"))
            assertEquals(action, aiWebRequest("""{"id":"1","action":"$action"}""").getString("action"))
        for (action in listOf("exec", "attachment.chunk", "configure", "openFile", "javascript")) {
            assertThrows(IllegalArgumentException::class.java) { aiWebRequest("""{"id":"2","action":"$action"}""") }
        }
        assertEquals("src/main.ts", aiArtifactRelativePath("D:\\work", "D:\\work\\src\\main.ts"))
        for (path in listOf("../secret", "D:\\workspace\\file", "\\\\server\\file", "/etc/passwd", "safe.txt:secret")) {
            assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("D:\\work", path) }
        }
    }

    @Test fun fileDirectoryUsesSessionComputerAndRejectsPathsOutsideWorkspace() {
        val value = aiArtifactLocation(session(), "output/site/index.html")
        assertEquals(pc, value.pcId); assertEquals(id, value.sessionId)
        assertEquals("D:\\work\\output\\site", value.directory)
        assertEquals("D:\\work\\output\\site\\index.html", value.filePath)
        assertEquals(value, aiArtifactLocation(session(), "d:\\WORK\\output\\site\\index.html"))
        assertEquals("D:\\", aiArtifactLocation(session().copy(directory = "D:\\"), "index.html").directory)
        for (path in listOf("../secret.txt", "out/../../secret", "out//file", "./file", "D:\\other\\file", "E:\\work\\file", "\\\\host\\share\\file", "safe:stream", "out/*"))
            assertThrows(IllegalArgumentException::class.java) { aiArtifactLocation(session(), path) }
        assertThrows(IllegalArgumentException::class.java) { aiArtifactLocation(session().copy(directory = "D:"), "file.txt") }
    }

    @Test fun artifactLocationsUseHostSyntaxForEveryAgent() {
        for (agent in listOf("Codex", "Claude Code", "ZCode", "Grok Build", "OpenCode", "future-agent")) {
            val mac = session().copy(agent = agent, directory = "/Users/test/项目 Work/")
            val location = aiArtifactLocation(mac, "/Users/test/项目 Work/输出 目录/嵌套/结果 文件.html")
            assertEquals(pc, location.pcId); assertEquals(id, location.sessionId)
            assertEquals("/Users/test/项目 Work/输出 目录/嵌套", location.directory)
            assertEquals("/Users/test/项目 Work/输出 目录/嵌套/结果 文件.html", location.filePath)
            assertEquals(location, aiArtifactLocation(mac, "输出 目录/嵌套/结果 文件.html"))
            assertEquals("/Users/test/项目 Work", aiArtifactLocation(mac, "结果.html").directory)

            val windows = mac.copy(directory = "C:\\项目 Work\\")
            val winLocation = aiArtifactLocation(windows, "c:/项目 work/输出 目录/嵌套/结果 文件.html")
            assertEquals(pc, winLocation.pcId); assertEquals(id, winLocation.sessionId)
            assertEquals("C:\\项目 Work\\输出 目录\\嵌套", winLocation.directory)
            assertEquals("C:\\项目 Work\\输出 目录\\嵌套\\结果 文件.html", winLocation.filePath)
        }
    }

    @Test fun posixPathsKeepCaseAndLiteralBackslashes() {
        val directory = "/Users/me/Work"
        for (file in listOf("outputs\\draft/结果\\1.txt", "..\\literal.txt", "name:part.txt", "NUL.txt", "notes. ")) {
            assertEquals(file, aiArtifactRelativePath(directory, file))
            assertEquals(file, aiArtifactRelativePath(directory, "$directory/$file"))
            assertEquals(file, aiArtifactRelativePath(directory, aiArtifactRelativePath(directory, "$directory/$file")))
            assertEquals("$directory/$file", aiArtifactLocation(session().copy(directory = directory), file).filePath)
        }
        assertEquals("/Users/me/Work/outputs\\draft", aiArtifactLocation(session().copy(directory = directory), "outputs\\draft/结果.txt").directory)
        val literalRoot = "/Users/me/项目\\草稿"
        assertEquals("out.txt", aiArtifactRelativePath(literalRoot, "$literalRoot/out.txt"))
        for (path in listOf("/users/me/Work/file", "/Users/me/work/file", "/Users/me/Workspace/file", "/etc/passwd",
            "C:\\Users\\me\\Work\\file", "C:/Users/me/Work/file", "C:file", "\\\\server\\share\\file", "//server/share/file",
            "$directory/C:file", "$directory/C:/file", "$directory/\\\\server\\share\\file")) {
            assertThrows("Accepted foreign path: $path", IllegalArgumentException::class.java) { aiArtifactRelativePath(directory, path) }
        }
        assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("/work/é", "/work/e\u0301/file") }
    }

    @Test fun artifactFilesystemRootsRemainAbsolute() {
        for (root in listOf("D:\\", "D:/")) {
            val location = aiArtifactLocation(session().copy(directory = root), "结果.txt")
            assertEquals("D:\\", location.directory); assertEquals("D:\\结果.txt", location.filePath)
            assertEquals(location, aiArtifactLocation(session().copy(directory = root), "d:/结果.txt"))
        }
        val mac = session().copy(directory = "/")
        val rootFile = aiArtifactLocation(mac, "/结果.txt")
        assertEquals("/", rootFile.directory); assertEquals("/结果.txt", rootFile.filePath)
        assertEquals("Users/me/输出 文件.txt", aiArtifactRelativePath("/", "/Users/me/输出 文件.txt"))
        for (path in listOf("C:/file", "C:\\file", "//server/share/file", "\\\\server\\share\\file"))
            assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("/", path) }
    }

    @Test fun uncArtifactsStayInsideTheSelectedShare() {
        val unc = session().copy(directory = "\\\\Host\\Shared Dir\\项目\\")
        val location = aiArtifactLocation(unc, "//host/shared dir/项目/输出/文件.txt")
        assertEquals(pc, location.pcId); assertEquals(id, location.sessionId)
        assertEquals("\\\\Host\\Shared Dir\\项目\\输出", location.directory)
        assertEquals("\\\\Host\\Shared Dir\\项目\\输出\\文件.txt", location.filePath)
        assertEquals(location, aiArtifactLocation(unc, "输出\\文件.txt"))
        assertEquals("\\\\Host\\Shared Dir", aiArtifactLocation(unc.copy(directory = "//Host/Shared Dir"), "file.txt").directory)
        for (path in listOf("//other/Shared Dir/项目/file", "//Host/Shared Dir2/项目/file", "//Host/Shared Dir/项目2/file",
            "\\\\?\\C:\\file", "\\\\.\\pipe\\file", "C:\\file", "\\file", "/file", "../file"))
            assertThrows(IllegalArgumentException::class.java) { aiArtifactLocation(unc, path) }
    }

    @Test fun artifactPathsRejectTraversalInvalidRootsAndWindowsAliases() {
        for (root in listOf("/Users/me/Work", "C:\\Work", "//Host/Share/Work")) {
            for (path in listOf("", ".", "..", "../secret", "out/../secret", "out/./file", "./file", "out//file", "out/", "bad\nfile", "bad\u0085file", "bad\u0000file", "a".repeat(2049)))
                assertThrows("Accepted $path under $root", IllegalArgumentException::class.java) { aiArtifactRelativePath(root, path) }
            for (path in listOf(root, "$root/", "$root/../secret"))
                assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath(root, path) }
        }
        for (path in listOf("safe.txt:stream", "nested/file::\$DATA", "..\\secret", "out\\..\\file", "out\\.\\file", "out\\\\file", "C:file",
            "C:\\Other\\file", "D:\\Work\\file", "file.", "file ", "NUL", "nul.txt", "CON .txt", "COM1.log", "LPT².txt", "CONIN$",
            "out/*", "out/?", "out/<", "out/>file", "out/\"", "out/pipe|file", "\\\\?\\C:\\Work\\file", "\\\\.\\pipe\\file"))
            assertThrows("Accepted Windows path: $path", IllegalArgumentException::class.java) { aiArtifactRelativePath("C:\\Work", path) }
        for (root in listOf("", "relative", "D:", "D:work", "\\work", "/Users/me/../Work", "/Users//Work", "C:\\work\\..", "C:\\work.",
            "\\\\server", "//server/../share", "//server/share:stream", "\\\\?\\C:\\Work", "\\\\.\\pipe", "/bad\nroot", "/" + "a".repeat(4096)))
            assertThrows("Accepted root: $root", IllegalArgumentException::class.java) { aiArtifactRelativePath(root, "file.txt") }
    }

    @Test fun artifactSeparatorsRemainStructuralBesideUnicodeCombiningMarks() {
        val root = "/Users/me/Work"; val file = "输出/\u0301结果.txt"
        assertEquals(file, aiArtifactRelativePath(root, "$root/$file"))
        assertEquals("$root/输出", aiArtifactLocation(session().copy(directory = root), file).directory)
        assertEquals("file", aiArtifactRelativePath("/\u0301Work", "/\u0301Work/file"))
        assertEquals(file, aiArtifactRelativePath("C:\\Work", "输出\\\u0301结果.txt"))
        assertEquals("C:\\Work\\输出\\\u0301结果.txt", aiArtifactLocation(session().copy(directory = "C:\\Work"), file).filePath)
        for (path in listOf("../\u0301secret", "out/../\u0301secret", "./\u0301file", "/\u0301outside", "//\u0301host/share/file")) {
            assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath(root, path) }
            assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("C:\\Work", path) }
        }
        for (path in listOf("safe:\u0301stream", "CON.\u0301txt", "out/\u0301name:stream", "..\\\u0301secret"))
            assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("C:\\Work", path) }
        assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath("C:\u0301/Work", "file") }
        assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath(root, "\\\\\u0301host\\share\\file") }
    }

    @Test fun advertisedPathStyleControlsLocationsIndependentlyOfHostPlatformNames() {
        for (platform in listOf("macos", "linux", "unknown")) {
            val active = session().copy(directory = "/srv/项目 Work",
                capabilities = AiCapabilities(pathStyle = "posix", hostPlatform = platform))
            val file = "输出\\草稿/结果 文件.txt"
            val location = aiArtifactLocation(active, active.directory + "/" + file)
            assertEquals(pc, location.pcId); assertEquals(id, location.sessionId)
            assertEquals(active.directory + "/输出\\草稿", location.directory)
            assertEquals(active.directory + "/" + file, location.filePath)
            assertEquals(file, aiArtifactRelativePath(active.directory, location.filePath, active.capabilities.pathStyle))
            assertThrows(IllegalArgumentException::class.java) { aiArtifactLocation(active, "/SRV/项目 Work/输出.txt") }
            val encoded = AiWebUpdates().encode(active, false, false, false, 1f).getJSONObject("session")
            assertEquals("posix", encoded.getString("pathStyle")); assertEquals(platform, encoded.getString("hostPlatform"))
        }
        for (platform in listOf("windows", "unknown")) {
            val active = session().copy(directory = "D:\\项目 Work",
                capabilities = AiCapabilities(pathStyle = "windows", hostPlatform = platform))
            val location = aiArtifactLocation(active, "d:/项目 work/输出 目录/结果.txt")
            assertEquals("D:\\项目 Work\\输出 目录", location.directory)
            assertEquals("输出 目录/结果.txt", aiArtifactRelativePath(active.directory, location.filePath, "windows"))
        }
    }

    @Test fun advertisedPathStyleRejectsUnsupportedStylesAndMismatchedRoots() {
        val conflicts = listOf("windows" to "/srv/work", "windows" to "/", "posix" to "D:\\Work",
            "posix" to "D:/", "posix" to "//host/share", "posix" to "\\\\host\\share")
        for ((style, directory) in conflicts) {
            assertThrows("Accepted $style root $directory", IllegalArgumentException::class.java) {
                aiArtifactRelativePath(directory, "file.txt", style)
            }
            assertThrows(IllegalArgumentException::class.java) {
                aiArtifactLocation(session().copy(directory = directory, capabilities = AiCapabilities(pathStyle = style)), "file.txt")
            }
        }
        for (style in listOf("unsupported", "unknown", "macos", "linux", "plan9", "Windows", "POSIX", " ")) {
            for (directory in listOf("D:\\Work", "/srv/work", "//host/share")) {
                val active = session().copy(directory = directory, capabilities = AiCapabilities(pathStyle = style, hostPlatform = "unknown"))
                assertThrows(IllegalArgumentException::class.java) { aiArtifactRelativePath(directory, "file.txt", style) }
                assertThrows(IllegalArgumentException::class.java) { aiArtifactLocation(active, "file.txt") }
            }
        }
    }

    @Test fun missingPathStyleFallsBackOnlyToTheActualSessionDirectory() {
        for (platform in listOf("", "unknown", "macos", "linux", "windows")) {
            val capabilities = AiCapabilities(pathStyle = "", hostPlatform = platform)
            val posix = session().copy(directory = "/srv/work", capabilities = capabilities)
            assertEquals("/srv/work/结果.txt", aiArtifactLocation(posix, "结果.txt").filePath)
            val windows = session().copy(directory = "E:\\Work", capabilities = capabilities)
            assertEquals("E:\\Work\\结果.txt", aiArtifactLocation(windows, "结果.txt").filePath)
            assertThrows(IllegalArgumentException::class.java) { aiArtifactLocation(posix.copy(directory = "relative"), "结果.txt") }
        }
        for (directory in listOf("/", "/srv/work", "D:\\", "D:\\Work", "//host/share")) {
            assertEquals(aiArtifactRelativePath(directory, "out/结果.txt"), aiArtifactRelativePath(directory, "out/结果.txt", ""))
        }
    }

    @Test fun uiStateOnlyRestoresCurrentQuestions() {
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
    }

    @Test fun sessionUpdatesKeepHostFeatureGatesAndOptionalQuestionRequirements() {
        for (enabled in listOf(false, true)) {
            val questions = listOf(AiQuestion("required", "Required", emptyList(), false),
                AiQuestion("optional", "Optional", emptyList(), false, required = false))
            val current = session().copy(capabilities = AiCapabilities(agentFeatures = enabled),
                pending = listOf(AiPending("request", "question", "", questions, true)))
            val encoded = AiWebUpdates().encode(current, false, false, false, 1f).getJSONObject("session")
            assertEquals(enabled, encoded.getBoolean("agentFeatures"))
            val actual = encoded.getJSONArray("pending").getJSONObject(0).getJSONArray("questions")
            assertTrue(actual.getJSONObject(0).getBoolean("required"))
            assertFalse(actual.getJSONObject(1).getBoolean("required"))
        }
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

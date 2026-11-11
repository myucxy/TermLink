package dev.termlink.product

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import java.io.IOException
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
class CliWorkspaceTest {
    @Test fun hostPathsAreOpaqueAndMissingReceiptsRetryTheOriginalOperation(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("cli-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val requests = mutableListOf<String>()
        var loseReceipt = true
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body)
                if (request.getString("action") == "catalog") return """{"hostId":"host","agents":[],"sessions":[],"cliWorkspaceCreation":true}"""
                requests += body
                assertFalse(request.has("directory"))
                if (loseReceipt) throw IOException("lost receipt")
                return JSONObject().put("hostId", "host").put("operationId", request.getString("operationId"))
                    .put("directory", "/Users/test/AiWorkspaces/cli_" + request.getString("operationId")).toString()
            }
        }
        var workspace = AiWorkspace(repository, scope, prefs)
        try {
            workspace.refresh(pc)
            assertTrue(workspace.catalogs.getValue(pc).cliWorkspaceCreation)
            assertNull(workspace.createCliWorkspace(pc))
            assertTrue(workspace.isUncertain(pc))
            val original = requests.single()
            workspace.retire()
            workspace = AiWorkspace(repository, scope, prefs)
            // The persisted pending request must not block read-only discovery.
            // Do not seed a catalog: process recreation loses that in-memory state.
            workspace.refresh(pc)
            assertTrue(workspace.isCliWorkspaceUncertain(pc))
            assertEquals(listOf(original), requests)
            assertTrue(prefs.contains("pending"))
            workspace.refresh(pc, "never-poll-while-pending")
            assertEquals(listOf(original), requests)
            loseReceipt = false
            val first = workspace.createCliWorkspace(pc)
            assertTrue(first!!.startsWith("/Users/test/"))
            assertEquals(listOf(original, original), requests)
            assertFalse(workspace.isBlocked(pc))
            assertFalse(prefs.contains("pending"))
            assertNotEquals(first, workspace.createCliWorkspace(pc))
            assertNotEquals(original, requests.last())
            loseReceipt = true
            workspace.mutate(pc, "send")!!.await()
            assertFalse(workspace.isCliWorkspaceUncertain(pc))
            assertNull(workspace.createCliWorkspace(pc))
            assertEquals(4, requests.size)
            workspace.catalogs[pc] = workspace.catalogs.getValue(pc).copy(cliWorkspaceCreation = false)
            assertNull(workspace.createCliWorkspace(pc))
            assertEquals(4, requests.size)
        } finally { workspace.retire(); scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun mismatchedHostOrOperationReceiptDoesNotReleaseThePendingRequest(): Unit = runBlocking {
        for ((host, operation, directory) in listOf(Triple("wrong", "same", "/Users/test/cli"),
            Triple("host", "wrong", "/Users/test/cli"), Triple("host", "same", "bad\npath"))) {
            val pc = UUID.randomUUID()
            val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("cli-$pc", Context.MODE_PRIVATE)
            val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
            val repository = object : ProductRepository by UnavailableProductRepository() {
                override suspend fun aiRequest(pcId: UUID, body: String): String {
                    val request = JSONObject(body)
                    return JSONObject().put("hostId", host).put("operationId", if (operation == "same") request.getString("operationId") else operation)
                        .put("directory", directory).toString()
                }
            }
            val workspace = AiWorkspace(repository, scope, prefs)
            try {
                workspace.catalogs[pc] = AiCatalog("host", emptyList(), emptyList(), cliWorkspaceCreation = true)
                assertNull(workspace.createCliWorkspace(pc))
                assertTrue(workspace.isUncertain(pc))
                assertTrue(prefs.contains("pending"))
            } finally { workspace.retire(); scope.cancel(); prefs.edit().clear().commit() }
        }
    }

    @Test fun discoveryOfChangedHostPreservesTheOriginalPendingBodyUntilExplicitRetry(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("cli-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val requests = mutableListOf<String>()
        var host = "original-host"
        var loseReceipt = true
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body)
                if (request.getString("action") == "catalog") return JSONObject().put("hostId", host)
                    .put("agents", org.json.JSONArray()).put("sessions", org.json.JSONArray()).put("cliWorkspaceCreation", true).toString()
                requests += body
                if (loseReceipt) throw IOException("lost receipt")
                return JSONObject().put("hostId", host).put("error", "HOST_CHANGED").toString()
            }
        }
        var workspace = AiWorkspace(repository, scope, prefs)
        try {
            workspace.refresh(pc)
            assertNull(workspace.createCliWorkspace(pc))
            val original = requests.single()
            workspace.retire()
            workspace = AiWorkspace(repository, scope, prefs)
            host = "replacement-host"
            workspace.refresh(pc)
            assertEquals(host, workspace.catalogs.getValue(pc).hostId)
            assertTrue(workspace.isUncertain(pc))
            assertFalse(workspace.isCliWorkspaceUncertain(pc))
            assertEquals(listOf(original), requests)
            assertNull(workspace.createCliWorkspace(pc))
            assertEquals(listOf(original), requests)
            loseReceipt = false
            workspace.retryUncertain(pc)
            withTimeout(1_000) { while (workspace.isBusy(pc)) yield() }
            assertEquals(listOf(original, original), requests)
            assertEquals("original-host", JSONObject(requests.last()).getString("hostId"))
            assertFalse(workspace.isBlocked(pc))
            assertFalse(prefs.contains("pending"))
            assertNull(workspace.catalogs[pc])
        } finally { workspace.retire(); scope.cancel(); prefs.edit().clear().commit() }
    }

    @Test fun lateReadOnlyDiscoveryCannotOverwriteACompletedReceipt(): Unit = runBlocking {
        val pc = UUID.randomUUID()
        val prefs = ApplicationProvider.getApplicationContext<Context>().getSharedPreferences("cli-$pc", Context.MODE_PRIVATE)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        var loseReceipt = true
        var delayCatalog = false
        val requested = CompletableDeferred<Unit>()
        val catalogResult = CompletableDeferred<String>()
        val mutations = mutableListOf<String>()
        val repository = object : ProductRepository by UnavailableProductRepository() {
            override suspend fun aiRequest(pcId: UUID, body: String): String {
                val request = JSONObject(body)
                if (request.getString("action") == "catalog") {
                    if (delayCatalog) { requested.complete(Unit); return catalogResult.await() }
                    return """{"hostId":"host","agents":[],"sessions":[],"cliWorkspaceCreation":true}"""
                }
                mutations += body
                if (loseReceipt) throw IOException("lost receipt")
                return JSONObject().put("hostId", "host").put("operationId", request.getString("operationId"))
                    .put("directory", "/Users/test/AiWorkspaces/cli-original").toString()
            }
        }
        val workspace = AiWorkspace(repository, scope, prefs)
        try {
            workspace.refresh(pc)
            assertNull(workspace.createCliWorkspace(pc))
            val original = mutations.single()
            delayCatalog = true
            val discovery = async { workspace.refresh(pc) }
            withTimeout(1_000) { requested.await() }
            loseReceipt = false
            workspace.retryUncertain(pc)
            withTimeout(1_000) { while (workspace.isBusy(pc)) yield() }
            catalogResult.complete("""{"hostId":"stale-host","agents":[],"sessions":[],"cliWorkspaceCreation":false}""")
            discovery.await()
            assertEquals("host", workspace.catalogs.getValue(pc).hostId)
            assertTrue(workspace.catalogs.getValue(pc).cliWorkspaceCreation)
            assertFalse(workspace.isBlocked(pc))
            assertEquals(listOf(original, original), mutations)
            assertEquals("/Users/test/AiWorkspaces/cli-original", workspace.createCliWorkspace(pc))
            assertEquals(listOf(original, original), mutations)
        } finally { catalogResult.cancel(); workspace.retire(); scope.cancel(); prefs.edit().clear().commit() }
    }
}

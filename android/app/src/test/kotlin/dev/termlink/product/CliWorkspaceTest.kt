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
            workspace.catalogs[pc] = AiCatalog("host", emptyList(), emptyList(), cliWorkspaceCreation = true)
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
}

package dev.termlink.network.production

import dev.termlink.crypto.DeviceIdentity
import java.io.IOException
import java.util.UUID
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Test

class ProductionApiAuthenticationTest {
    @Test
    fun retriesOnlyRejectedMutationWithSameBodyAndKeyAndFreshProof() {
        val identity = DeviceIdentity.generate()
        try {
            MockWebServer().use { server ->
                server.enqueue(MockResponse().setBody("{}"))
                server.enqueue(MockResponse().setResponseCode(401).setBody("<html>Unauthorized</html>"))
                server.enqueue(MockResponse().setBody("{}"))
                var token = "old"
                var refreshes = 0
                val api = ProductionApiClient(
                    server.url("/").toString(), AccessTokenProvider { token }, UUID.randomUUID(), identity,
                    allowCleartextForLocalTest = true,
                    authenticationRefresher = { rejected ->
                        assertEquals("old", rejected)
                        refreshes++
                        token = "fresh"
                        token
                    },
                    onAuthenticationRejected = { error("Refreshed token must be accepted") },
                )
                api.post("/v1/first", buildJsonObject { put("action", "create") }, "first-key")
                api.post("/v1/second", buildJsonObject { put("action", "delete") }, "second-key")

                assertEquals(1, refreshes)
                assertEquals(3, server.requestCount)
                assertEquals("/v1/first", server.takeRequest().path)
                val rejected = server.takeRequest()
                val retried = server.takeRequest()
                assertEquals("/v1/second", retried.path)
                assertEquals(rejected.method, retried.method)
                assertEquals(rejected.body.readUtf8(), retried.body.readUtf8())
                assertEquals("second-key", retried.getHeader("Idempotency-Key"))
                assertEquals("Bearer old", rejected.getHeader("Authorization"))
                assertEquals("Bearer fresh", retried.getHeader("Authorization"))
                assertNotEquals(rejected.getHeader("X-TermLink-Nonce"), retried.getHeader("X-TermLink-Nonce"))
                assertNotEquals(rejected.getHeader("X-TermLink-Signature"), retried.getHeader("X-TermLink-Signature"))
            }
        } finally {
            identity.destroy()
        }
    }

    @Test
    fun repeatedUnauthorizedIsBoundedAndReportsTheExactRetriedToken() {
        val identity = DeviceIdentity.generate()
        try {
            MockWebServer().use { server ->
                server.enqueue(MockResponse().setResponseCode(401))
                server.enqueue(MockResponse().setResponseCode(401).setBody("[]"))
                var refreshes = 0
                val rejections = mutableListOf<String>()
                val api = ProductionApiClient(
                    server.url("/").toString(), AccessTokenProvider { "old" }, UUID.randomUUID(), identity,
                    allowCleartextForLocalTest = true,
                    authenticationRefresher = { refreshes++; "fresh" },
                    onAuthenticationRejected = { rejections += it },
                )
                val error = assertThrows(TermLinkApiException::class.java) { api.get("/v1/pcs") }
                assertEquals(401, error.status)
                assertEquals("HTTP_401", error.code)
                assertEquals(1, refreshes)
                assertEquals(listOf("fresh"), rejections)
                assertEquals(2, server.requestCount)
            }
        } finally {
            identity.destroy()
        }
    }

    @Test
    fun permissionAndServerFailuresDoNotRefreshOrInvalidate() {
        val identity = DeviceIdentity.generate()
        try {
            MockWebServer().use { server ->
                val api = ProductionApiClient(
                    server.url("/").toString(), AccessTokenProvider { "valid" }, UUID.randomUUID(), identity,
                    allowCleartextForLocalTest = true,
                    authenticationRefresher = { error("Must not refresh") },
                    onAuthenticationRejected = { error("Must not sign out") },
                )
                for (status in listOf(403, 500, 502, 503)) {
                    server.enqueue(MockResponse().setResponseCode(status).setBody("<html>failure</html>"))
                    assertEquals(status, assertThrows(TermLinkApiException::class.java) { api.delete("/v1/item") }.status)
                }
                for (code in listOf("DEVICE_REVOKED", "DEVICE_DISABLED", "STALE_PROOF", "REPLAYED_PROOF", "INVALID_DEVICE_PROOF")) {
                    server.enqueue(MockResponse().setResponseCode(401).setBody("""{"error":{"code":"$code"}}"""))
                    assertEquals(code, assertThrows(TermLinkApiException::class.java) { api.get("/v1/devices/self") }.code)
                }
                assertEquals(9, server.requestCount)
            }
        } finally {
            identity.destroy()
        }
    }

    @Test
    fun refreshTransportFailureDoesNotInvalidateOrReplayMutation() {
        val identity = DeviceIdentity.generate()
        try {
            MockWebServer().use { server ->
                server.enqueue(MockResponse().setResponseCode(401))
                val unavailable = IOException("token endpoint unavailable")
                val api = ProductionApiClient(
                    server.url("/").toString(), AccessTokenProvider { "old" }, UUID.randomUUID(), identity,
                    allowCleartextForLocalTest = true,
                    authenticationRefresher = { throw unavailable },
                    onAuthenticationRejected = { error("Network failure must not sign out") },
                )
                assertSame(unavailable, assertThrows(IOException::class.java) { api.delete("/v1/item") })
                assertEquals(1, server.requestCount)
            }
        } finally {
            identity.destroy()
        }
    }
}

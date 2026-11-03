package dev.termlink.network.production

import dev.termlink.crypto.DeviceIdentity
import java.util.UUID
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.*
import org.junit.Test

class ProductionConnectionErrorTest {
    @Test fun additiveConnectionStagesPreserveStatusCodeAndTrace() {
        val identity = DeviceIdentity.generate()
        try {
            MockWebServer().use { server ->
                val api = ProductionApiClient(server.url("/").toString(), AccessTokenProvider { "token" },
                    UUID.randomUUID(), identity, allowCleartextForLocalTest = true)
                for (stage in listOf("configuration", "device_registration", "device_identity", "pc_metadata",
                    "push_registration", "relay_handshake", "file_transfer_handshake")) {
                    server.enqueue(MockResponse().setResponseCode(401).setBody("""{"error":{"code":"UNAUTHENTICATED","trace_id":"trace","message_key":"auth.unauthenticated","stage":"$stage"}}"""))
                    val error = assertThrows(TermLinkApiException::class.java) { api.get("/v1/devices/self") }
                    assertEquals(401, error.status); assertEquals("UNAUTHENTICATED", error.code)
                    assertEquals("trace", error.traceId); assertEquals(stage, error.stage)
                    assertEquals("auth.unauthenticated", error.messageKey)
                }
                for (body in listOf("""{"error":{"code":"DENIED"}}""",
                    """{"error":{"code":"DENIED","stage":"private path","message_key":"secret account data"}}""",
                    """{"error":{"code":"DENIED","stage":{},"message_key":[]}}""", "<html>unavailable</html>")) {
                    server.enqueue(MockResponse().setResponseCode(503).setBody(body))
                    val error = assertThrows(TermLinkApiException::class.java) { api.get("/v1/pcs") }
                    assertEquals(503, error.status); assertNull(error.stage); assertNull(error.messageKey)
                }
            }
        } finally { identity.destroy() }
    }
}

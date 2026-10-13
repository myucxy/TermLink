package dev.termlink.network.production

import dev.termlink.crypto.DeviceIdentity
import java.util.UUID
import kotlinx.serialization.json.buildJsonObject
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ProductionPcCommandCapabilitiesTest {
    @Test fun enhancedCommandHeaderIsExactGetPcsOnly() {
        val identity = DeviceIdentity.generate()
        try {
            MockWebServer().use { server ->
                repeat(4) { server.enqueue(MockResponse().setBody("{}")) }
                val api = ProductionApiClient(server.url("/").toString(), AccessTokenProvider { "token" },
                    UUID.randomUUID(), identity, allowCleartextForLocalTest = true)
                api.get("/v1/pcs")
                api.get("/v1/pcs?limit=1")
                api.get("/v1/devices")
                api.post("/v1/pcs", buildJsonObject { })
                assertEquals("powershell-7-enhanced",
                    server.takeRequest().getHeader("X-TermLink-PC-Command-Capabilities"))
                repeat(3) {
                    assertNull(server.takeRequest().getHeader("X-TermLink-PC-Command-Capabilities"))
                }
            }
        } finally { identity.destroy() }
    }
}

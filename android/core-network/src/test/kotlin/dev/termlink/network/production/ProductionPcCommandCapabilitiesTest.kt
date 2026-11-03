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
                val request = server.takeRequest()
                assertEquals("powershell-7-enhanced", request.getHeader("X-TermLink-PC-Command-Capabilities"))
                assertEquals("posix-shells-v1", request.getHeader("X-TermLink-PC-Shell-Capabilities"))
                repeat(3) {
                    val other = server.takeRequest()
                    assertNull(other.getHeader("X-TermLink-PC-Command-Capabilities"))
                    assertNull(other.getHeader("X-TermLink-PC-Shell-Capabilities"))
                }
            }
        } finally { identity.destroy() }
    }
}

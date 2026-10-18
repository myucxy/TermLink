package dev.termlink.network.production

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ProductionAiAgentsTest {
    @Test fun onlyNativeImportAndResumeWaitForDesktopHistoryReconstruction() {
        assertEquals(120_000L, aiAgentRequestTimeoutMillis("""{"action":"import-native"}"""))
        assertEquals(120_000L, aiAgentRequestTimeoutMillis("""{"action":"resume"}"""))
        for (action in listOf("send", "catalog", "native-history", "control", "end", "watch", "")) {
            assertEquals(40_000L, aiAgentRequestTimeoutMillis("""{"action":"$action"}"""))
        }
        assertEquals(40_000L, aiAgentRequestTimeoutMillis("{}"))
    }
    @Test fun malformedBodiesFailBeforeSelectingTimeout() {
        for (body in listOf("{", "[]", "null")) {
            assertThrows(IllegalArgumentException::class.java) { aiAgentRequestTimeoutMillis(body) }
        }
    }
}

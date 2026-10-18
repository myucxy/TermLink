package dev.termlink.network.production

import org.junit.Assert.assertEquals
import org.junit.Test

class ProductionAiAgentsTest {
    @Test fun onlyNativeImportAndResumeWaitForDesktopHistoryReconstruction() {
        assertEquals(120_000L, aiAgentRequestTimeoutMillis("import-native"))
        assertEquals(120_000L, aiAgentRequestTimeoutMillis("resume"))
        for (action in listOf("send", "catalog", "native-history", "control", "end", "watch", "")) {
            assertEquals(40_000L, aiAgentRequestTimeoutMillis(action))
        }
    }
}

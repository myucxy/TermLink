package dev.termlink.product

import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test

class HostCapabilitiesTest {
    private fun pc(platform: String, capabilities: Set<String>) = PcSummary(
        UUID.randomUUID(), UUID.randomUUID(), "Host", "test", remoteRoutingEnabled = true,
        online = true, lastSeenAt = null, platform = platform, capabilities = capabilities,
    )

    @Test fun catalogRequirementFollowsCapabilitiesAcrossPlatforms() {
        for (platform in listOf("windows", "macos", "linux")) {
            assertFalse(requiresHostSessionCatalog(pc(platform, emptySet())))
            assertFalse(requiresHostSessionCatalog(pc(platform, setOf("ai_agent_v1"))))
            for (capability in listOf(PcCapabilityIds.REALTIME_TERMINAL_V1,
                PcCapabilityIds.TERMINAL_COMMANDS_V1, PcCapabilityIds.HOST_SESSION_CATALOG_V1)) {
                assertTrue(requiresHostSessionCatalog(pc(platform, setOf(capability))))
            }
        }
    }

    @Test fun posixCommandsUseHostMetadataAndExplicitlyMissingCapabilityHidesThem() {
        val host = pc("macos", setOf(PcCapabilityIds.TERMINAL_COMMANDS_V1)).copy(
            supportedShells = setOf("zsh", "bash", "sh", "fish", "pwsh", "unknown-shell"),
        )
        assertEquals(listOf("bash", "zsh", "sh", "fish", "pwsh"), supportedRemoteTerminalCommands(host).map { it.id })
        assertTrue(supportedRemoteTerminalCommands(host.copy(capabilities = setOf("ai_agent_v1"))).isEmpty())
    }
}

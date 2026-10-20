package dev.termlink.product

import org.junit.Assert.*
import org.junit.Test

class AiAgentBrandTest {
    @Test fun everyBrandDisplayNameResolvesBackToItsOwnIcon() {
        assertEquals(setOf("codex", "claude", "zcode", "grok", "opencode"), aiAgentBrands.keys)
        assertEquals(aiAgentBrands.size, aiAgentBrands.values.map { it.icon }.toSet().size)
        aiAgentBrands.forEach { (id, brand) ->
            assertEquals(id, aiAgentId(brand.name))
            assertEquals(id, aiAgentId("", "  ${brand.name.uppercase()}  "))
            assertEquals(brand.name, aiAgentName(brand.name))
            assertEquals(brand, aiAgentBrands[aiAgentId(brand.name)])
        }
    }

    @Test fun knownAliasesResolveWithoutMisbrandingAnUnknownAgent() {
        mapOf("grok-build" to "grok", "Grok Build" to "grok", "open-code" to "opencode",
            "Open Code" to "opencode", "Claude-Code" to "claude").forEach { (alias, id) ->
            assertEquals(id, aiAgentId(alias))
        }
        assertEquals("future-agent", aiAgentId("future-agent"))
        assertNull(aiAgentBrands[aiAgentId("future-agent")])
        assertEquals("AI", aiAgentName(""))
    }
}

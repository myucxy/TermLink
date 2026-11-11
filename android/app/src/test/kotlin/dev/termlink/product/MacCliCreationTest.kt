package dev.termlink.product

import dev.termlink.model.PcCapabilityIds
import dev.termlink.model.PcSummary
import dev.termlink.model.TerminalCommandCapability
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test

class MacCliCreationTest {
    private val macCapabilities = setOf(PcCapabilityIds.REALTIME_TERMINAL_V1, PcCapabilityIds.TERMINAL_COMMANDS_V1)
    private val zsh = TerminalCommandCapability("zsh", "Z shell", "zsh", "detected")
    private fun pc(platform: String = "macos", capabilities: Set<String> = macCapabilities,
        commands: List<TerminalCommandCapability> = listOf(zsh)) = PcSummary(
        UUID.randomUUID(), UUID.randomUUID(), "Host", "0.3.16.146", remoteRoutingEnabled = true,
        online = true, lastSeenAt = null, platform = platform, capabilities = capabilities,
        terminalCommands = commands,
    )

    @Test fun terminalOnlyMacShowsCliWithoutAdvertisingFilesOrAi() {
        val host = pc()
        assertTrue(supportsRemoteTerminalCreation(host))
        assertEquals(listOf(zsh), supportedRemoteTerminalCommands(host))
        assertTrue(usesManualMacCliDirectory(host))
        assertFalse(PcCapabilityIds.FILE_OPERATIONS_V1 in host.capabilities)
        assertFalse("ai_agent_v1" in host.capabilities)
    }

    @Test fun eachMacCapabilityAndActualCommandMetadataAreRequired() {
        for (capabilities in listOf(emptySet(), setOf("ai_agent_v1"),
            setOf(PcCapabilityIds.REALTIME_TERMINAL_V1), setOf(PcCapabilityIds.TERMINAL_COMMANDS_V1))) {
            assertFalse(supportsRemoteTerminalCreation(pc(capabilities = capabilities)))
        }
        assertFalse(supportsRemoteTerminalCreation(pc(commands = emptyList())))
    }

    @Test fun macCanOfferAllAdvertisedPosixShellsWithoutWindowsFiltering() {
        val commands = listOf("bash", "zsh", "sh", "fish", "pwsh").map {
            TerminalCommandCapability(it, it, it, "detected")
        }
        val host = pc(commands = commands)
        assertTrue(supportsRemoteTerminalCreation(host))
        assertEquals(commands, supportedRemoteTerminalCommands(host))
    }

    @Test fun legacyWindowsFileAndCommandsCapabilitiesRemainSupported() {
        val powershell = TerminalCommandCapability("powershell-7-enhanced", "PowerShell 7", "powershell-7", "detected")
        val host = pc(platform = "windows", capabilities = setOf(PcCapabilityIds.FILE_OPERATIONS_V1,
            PcCapabilityIds.TERMINAL_COMMANDS_V1), commands = listOf(powershell))
        assertTrue(supportsRemoteTerminalCreation(host))
        assertFalse(usesManualMacCliDirectory(host))
        assertEquals(listOf(powershell), supportedRemoteTerminalCommands(host))
        assertFalse(supportsRemoteTerminalCreation(host.copy(capabilities = setOf(PcCapabilityIds.FILE_OPERATIONS_V1))))
    }

    @Test fun terminalOnlyExceptionDoesNotApplyToUnimplementedPlatforms() {
        for (platform in listOf("windows", "linux", "unknown", "")) {
            assertFalse(supportsRemoteTerminalCreation(pc(platform = platform)))
            assertFalse(usesManualMacCliDirectory(pc(platform = platform)))
        }
    }

    @Test fun explicitMissingCommandsCapabilityHidesMetadataAndLegacyShellFallback() {
        val host = pc(capabilities = setOf(PcCapabilityIds.REALTIME_TERMINAL_V1)).copy(supportedShells = setOf("zsh"))
        assertTrue(supportedRemoteTerminalCommands(host).isEmpty())
        assertFalse(supportsRemoteTerminalCreation(host))
    }

    @Test fun manualDirectoryAllowsLiteralUnicodeSpacesQuotesAndMetacharacters() {
        for (path in listOf("/", "/Users/test/中文 项目", "/tmp/'quotes' \"double\" ; $(literal) `literal`", "/tmp/a\\b", "/tmp/name ")) {
            assertTrue("An absolute path must stay data: $path", validMacCliDirectory(path))
        }
    }

    @Test fun manualDirectoryRejectsRelativeWindowsHomeAliasesAndControlCharacters() {
        for (path in listOf("", " ", "project", "./project", "../project", "~/project", "C:\\project", "\\\\host\\share",
            "/tmp/a\u0000b", "/tmp/a\nb", "/tmp/a\rb", "/tmp/a\tb", "/tmp/a\u007fb")) {
            assertFalse("Invalid directory was accepted: ${path.toCharArray().map(Char::code)}", validMacCliDirectory(path))
        }
    }

    @Test fun manualDirectoryLimitUsesUtf8BytesWithoutTrimmingOrNormalizing() {
        assertTrue(validMacCliDirectory("/" + "a".repeat(4095)))
        assertFalse(validMacCliDirectory("/" + "a".repeat(4096)))
        assertTrue(validMacCliDirectory("/" + "中".repeat(1365)))
        assertFalse(validMacCliDirectory("/" + "中".repeat(1366)))
    }
}

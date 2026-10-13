package dev.termlink.product

import dev.termlink.model.TerminalCommandCapability
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.assertThrows
import org.junit.Test

class TerminalCommandValidationTest {
    @Test fun acceptsLegacyDetectedExactEnhancedAndManualCustom() {
        listOf("command-prompt", "windows-powershell", "powershell-7", "wsl")
            .forEach { assertTrue(valid(it, it, "detected")) }
        assertTrue(valid("powershell-7-enhanced", "powershell-7", "detected"))
        assertTrue(valid("my-command", "custom", "manual"))
    }

    @Test fun rejectsInventedAliasesAndSourceShellConfusion() {
        listOf(
            Triple("powershell-7-enhanced", "custom", "detected"),
            Triple("powershell-7-enhanced", "wsl", "detected"),
            Triple("powershell-7-enhanced", "powershell-7", "manual"),
            Triple("powershell-7", "custom", "detected"),
            Triple("unknown", "unknown", "detected"),
            Triple("my-command", "powershell-7", "manual"),
            Triple("my-command", "custom", "unknown"),
        ).forEach { (id, shell, source) -> assertFalse(valid(id, shell, source)) }
        assertThrows(IllegalArgumentException::class.java) {
            requireValidTerminalCommandCapability(TerminalCommandCapability(
                "powershell-7-enhanced", "Shell", "custom", "detected"))
        }
    }

    private fun valid(id: String, shell: String, source: String) =
        isValidTerminalCommandCapability(TerminalCommandCapability(id, "Shell", shell, source))
}

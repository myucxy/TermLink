package dev.termlink.product

import dev.termlink.model.TerminalCommandCapability

/** Mirrors the server's exact detected command mapping; manual commands remain custom only. */
internal fun isValidTerminalCommandCapability(command: TerminalCommandCapability): Boolean = when (command.source) {
    "manual" -> command.shellType == "custom"
    "detected" -> command.id == command.shellType && command.id in setOf(
        "command-prompt", "windows-powershell", "powershell-7", "wsl",
    ) || command.id == "powershell-7-enhanced" && command.shellType == "powershell-7"
    else -> false
}

internal fun requireValidTerminalCommandCapability(command: TerminalCommandCapability) {
    require(isValidTerminalCommandCapability(command))
}

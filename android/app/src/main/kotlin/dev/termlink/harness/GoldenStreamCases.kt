package dev.termlink.harness

data class GoldenStreamCase(
    val id: String,
    val displayName: String,
) {
    val assetPath: String = "terminal-golden-streams/$id"
}

object GoldenStreamCases {
    val local: List<GoldenStreamCase> = listOf(
        GoldenStreamCase("ansi-unicode-pwsh-v1", "ANSI + Unicode · PowerShell 7"),
        GoldenStreamCase("smoke-cmd-v1", "Smoke · CMD"),
        GoldenStreamCase("smoke-powershell-v1", "Smoke · Windows PowerShell"),
        GoldenStreamCase("smoke-pwsh-v1", "Smoke · PowerShell 7"),
        GoldenStreamCase("smoke-wsl-v1", "Smoke · WSL"),
        GoldenStreamCase("terminal-features-pwsh-v1", "Terminal features · PowerShell 7"),
        GoldenStreamCase("tui-fzf-wsl-v1", "TUI · WSL fzf"),
        GoldenStreamCase("tui-htop-wsl-v1", "TUI · WSL htop"),
        GoldenStreamCase("tui-less-wsl-v1", "TUI · WSL less"),
        GoldenStreamCase("tui-vim-wsl-v1", "TUI · WSL Vim"),
    )

    val default: GoldenStreamCase = local.single {
        it.assetPath == GoldenStreamAssetLoader.defaultCase
    }
    val lanFixed: GoldenStreamCase = default

    fun find(id: String): GoldenStreamCase = local.firstOrNull { it.id == id } ?: default
}

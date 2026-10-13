using System;
using System.IO;
using System.Linq;
using System.Text;
using TermLink.Agent;

namespace TermLink.SessionHost;

/// <summary>Session-local hooks; no profile files, execution policy or PSReadLine key bindings change.</summary>
internal static class PowerShellShellIntegration
{
    internal static string Prepare(string commandLine, ManagedShellType shell)
    {
        if (shell != ManagedShellType.PowerShell7 && shell != ManagedShellType.WindowsPowerShell) return commandLine;
        var text = commandLine.Trim();
        var end = text.StartsWith('"') ? text.IndexOf('"', 1) + 1 : text.IndexOf(' ');
        if (end == 0) return commandLine;
        if (end < 0) end = text.Length;
        var executable = text.Substring(0, end).Trim('"');
        var name = Path.GetFileNameWithoutExtension(executable);
        if (!name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) && !name.Equals("powershell", StringComparison.OrdinalIgnoreCase)) return commandLine;
        var arguments = text.Substring(end).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        // Custom commands/files/noninteractive shells keep their exact launch semantics.
        if (arguments.Any(a => !a.Equals("-NoLogo", StringComparison.OrdinalIgnoreCase) &&
            !a.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase) && !a.Equals("-NoExit", StringComparison.OrdinalIgnoreCase))) return commandLine;
        var noExit = arguments.Any(a => a.Equals("-NoExit", StringComparison.OrdinalIgnoreCase)) ? "" : " -NoExit";
        return text + noExit + " -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
    }

    internal const string Script = """
if (-not $global:__TermLinkShellIntegration) {
    $global:__TermLinkOriginalPrompt = (Get-Command prompt -CommandType Function).ScriptBlock
    if (-not (Get-Command PSConsoleHostReadLine -CommandType Function -ErrorAction Ignore)) {
        Import-Module PSReadLine -ErrorAction SilentlyContinue
    }
    $readLine = Get-Command PSConsoleHostReadLine -CommandType Function -ErrorAction Ignore
    if ($readLine) {
        $global:__TermLinkOriginalReadLine = $readLine.ScriptBlock
        function global:PSConsoleHostReadLine {
            $line = & $global:__TermLinkOriginalReadLine
            [Console]::Write(([char]27 + ']133;C' + [char]27 + '\'))
            $global:__TermLinkCommandActive = $true
            return $line
        }
    }
    function global:prompt {
        param($previousSuccess = $?, $previousExit = $global:LASTEXITCODE)
        # Invoke first: assignments/Console.Write beforehand would hide $? from a custom prompt.
        $originalText = & $global:__TermLinkOriginalPrompt
        $esc = [char]27
        if ($global:__TermLinkCommandActive) {
            $code = 0
            # PowerShell's pipeline status is Boolean. LASTEXITCODE can be stale after a cmdlet failure.
            if (-not $previousSuccess) { $code = 1 }
            [Console]::Write(($esc + ']133;D;' + $code + $esc + '\'))
            $global:__TermLinkCommandActive = $false
        }
        [Console]::Write(($esc + ']133;A' + $esc + '\'))
        if ($pwd.Provider.Name -eq 'FileSystem' -and $pwd.ProviderPath -match '^[A-Za-z]:\\') {
            $path = $pwd.ProviderPath.Replace('\', '/')
            $segments = $path.Substring(3).Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }
            $uri = 'file:///' + $path.Substring(0, 3) + ($segments -join '/')
            [Console]::Write(($esc + ']7;' + $uri + $esc + '\'))
        } else {
            # Empty host root is inert and deliberately cannot become a Windows directory action.
            [Console]::Write(($esc + ']7;file:///' + $esc + '\'))
        }
        $global:LASTEXITCODE = $previousExit
        return (($originalText -join '') + $esc + ']133;B' + $esc + '\')
    }
    $global:__TermLinkShellIntegration = $true
}
""";
}

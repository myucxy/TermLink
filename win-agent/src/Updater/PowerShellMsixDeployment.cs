using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TermLink.Updater
{
    public sealed class PowerShellMsixDeployment : IUpdateDeployment
    {
        private const string InstallScript =
            "$ErrorActionPreference='Stop';" +
            "$p=Get-AppxPackage -Name $env:TERMLINK_UPDATE_IDENTITY|Sort-Object Version -Descending|Select-Object -First 1;" +
            "if($null -ne $p -and $p.Version.ToString() -eq $env:TERMLINK_UPDATE_VERSION){exit 0};" +
            "$a=@{Path=$env:TERMLINK_UPDATE_PACKAGE;ForceApplicationShutdown=$true;ErrorAction='Stop'};" +
            "if($env:TERMLINK_UPDATE_DOWNGRADE -eq '1'){$a.ForceUpdateFromAnyVersion=$true};" +
            "Add-AppxPackage @a;" +
            "$p=Get-AppxPackage -Name $env:TERMLINK_UPDATE_IDENTITY|Sort-Object Version -Descending|Select-Object -First 1;" +
            "if($null -eq $p -or $p.Version.ToString() -ne $env:TERMLINK_UPDATE_VERSION){throw 'Installed package version mismatch'}";

        private const string HealthScript =
            "$ErrorActionPreference='Stop';" +
            "$p=Get-AppxPackage -Name $env:TERMLINK_UPDATE_IDENTITY|Sort-Object Version -Descending|Select-Object -First 1;" +
            "if($null -eq $p -or $p.Version.ToString() -ne $env:TERMLINK_UPDATE_VERSION){exit 2};" +
            "$tray=Join-Path $p.InstallLocation 'TermLink.Tray.exe';" +
            "if(-not (Test-Path -LiteralPath $tray -PathType Leaf)){exit 3};" +
            "$args='--status-json \"'+$env:TERMLINK_UPDATE_STATUS+'\"';" +
            "Invoke-CommandInDesktopPackage -PackageFamilyName $p.PackageFamilyName -AppId 'TermLinkTray' -Command $tray -Args $args -ErrorAction Stop|Out-Null;" +
            "$deadline=[DateTime]::UtcNow.AddSeconds(30);" +
            "while(-not (Test-Path -LiteralPath $env:TERMLINK_UPDATE_STATUS -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 200};" +
            "if(-not (Test-Path -LiteralPath $env:TERMLINK_UPDATE_STATUS -PathType Leaf)){exit 4};" +
            "$s=Get-Content -Raw -LiteralPath $env:TERMLINK_UPDATE_STATUS|ConvertFrom-Json;" +
            "if(-not $s.Success){exit 5}";

        private const string StartTrayScript =
            "$ErrorActionPreference='Stop';" +
            "$p=Get-AppxPackage -Name $env:TERMLINK_UPDATE_IDENTITY|Sort-Object Version -Descending|Select-Object -First 1;" +
            "if($null -eq $p){exit 2};" +
            "$target='shell:AppsFolder\'+$p.PackageFamilyName+'!TermLinkTray';" +
            "Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList $target|Out-Null";

        public void Install(VerifiedUpdatePackage package, bool allowDowngrade)
        {
            var variables = Variables(package);
            variables["TERMLINK_UPDATE_PACKAGE"] = package.PackagePath;
            variables["TERMLINK_UPDATE_DOWNGRADE"] = allowDowngrade ? "1" : "0";
            var result = Invoke(InstallScript, variables, TimeSpan.FromMinutes(5));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("MSIX deployment failed: " + result.Error);
            }
        }

        public bool CheckHealth(VerifiedUpdatePackage package)
        {
            var statusPath = Path.Combine(Path.GetTempPath(), "TermLink-UpdateHealth-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var variables = Variables(package);
                variables["TERMLINK_UPDATE_STATUS"] = statusPath;
                return Invoke(HealthScript, variables, TimeSpan.FromSeconds(45)).ExitCode == 0;
            }
            finally
            {
                if (File.Exists(statusPath)) File.Delete(statusPath);
            }
        }

        public void StartTray(VerifiedUpdatePackage package)
        {
            var result = Invoke(StartTrayScript, Variables(package), TimeSpan.FromSeconds(15));
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("The updated Tray could not be started: " + result.Error);
            }
        }

        private static Dictionary<string, string> Variables(VerifiedUpdatePackage package)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TERMLINK_UPDATE_IDENTITY"] = package.Manifest.PackageIdentity,
                ["TERMLINK_UPDATE_VERSION"] = package.Manifest.Version
            };
        }

        private static ProcessResult Invoke(string script, IDictionary<string, string> variables, TimeSpan timeout)
        {
            var powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var start = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.Environment.Remove("PSModulePath");
            foreach (var variable in variables) start.Environment[variable.Key] = variable.Value;
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Windows PowerShell could not start.");
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    process.Kill(true);
                    throw new TimeoutException("Windows package deployment timed out.");
                }
                return new ProcessResult(process.ExitCode, Limit(output.GetAwaiter().GetResult() + " " + error.GetAwaiter().GetResult()));
            }
        }

        private static string Limit(string value)
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "no error details"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 1500 ? normalized : normalized.Substring(0, 1500);
        }

        private sealed class ProcessResult
        {
            public ProcessResult(int exitCode, string error)
            {
                ExitCode = exitCode;
                Error = error;
            }

            public int ExitCode { get; }
            public string Error { get; }
        }
    }
}

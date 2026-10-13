using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using TermLink.Agent;
using TermLink.LocalIpc;

internal static class GraphicsLauncherValidation
{
    internal static void Run()
    {
        var executable = Environment.ProcessPath!;
        var profile = new TerminalCommandProfile { ExecutablePath = executable, ManagedShell = "custom", Arguments = "" };
        var session = Guid.NewGuid();
        var legacy = ManagedTerminalLauncher.BuildSessionHostArgumentsForProfile(profile, Path.GetTempPath(), session);
        Require(!legacy.Contains("--terminal-state") && !legacy.Contains("--native-display") &&
            !legacy.Contains("--broker-captures"), "Legacy launcher defaults changed.");
        var graphics = ManagedTerminalLauncher.BuildSessionHostArgumentsForProfile(profile, Path.GetTempPath(), session,
            nativeDisplayExecutable: executable);
        Require(graphics.Take(legacy.Length).SequenceEqual(legacy), "Graphics launch changed shell arguments or session identity.");
        Require(graphics[^5] == "--native-display" && graphics[^4] == Path.GetFullPath(executable) &&
            graphics[^3] == "--terminal-state" && graphics[^2] == TerminalSemanticProfile.GraphicsUnicode16.StateContract &&
            graphics[^1] == "--broker-captures",
            "Native display was launched without its explicit graphics and Broker capture profile.");
        try
        {
            ManagedTerminalLauncher.BuildSessionHostArgumentsForProfile(profile, Path.GetTempPath(), session,
                nativeDisplayExecutable: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe"));
        }
        catch (ArgumentException)
        {
            ValidateEnhancedCommand();
            Console.WriteLine("Managed launcher preserves legacy commands and opt-in enhanced PowerShell 7.");
            return;
        }
        throw new InvalidOperationException("Missing native display executable was accepted.");
    }

    private static void ValidateEnhancedCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-enhanced-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousSettings = AppContext.GetData(TerminalCommandProfileStore.StoragePathAppContextKey);
        AppContext.SetData(TerminalCommandProfileStore.StoragePathAppContextKey,
            Path.Combine(root, "terminal-commands.json"));
        try
        {
            var host = Path.Combine(root, "TermLink.SessionHost.exe");
            var desktop = Path.Combine(root, "TermLink.exe");
            var shell = Path.Combine(root, "pwsh.exe");
            foreach (var path in new[] { host, desktop, shell })
                File.WriteAllBytes(path, new byte[] { 0x4d, 0x5a });
            ManagedTerminalLauncher Create(RecordingProcessStarter starter,
                string powerShell7, string nativeDisplay) => new(
                new WindowsPathService(), starter, () => shell,
                host, shell, shell, powerShell7, null,
                "termlink-enhanced-validation", nativeDisplay);

            var withoutDesktop = Create(new RecordingProcessStarter(), shell, null);
            Require(!withoutDesktop.AdvertisedCommands().Any(command =>
                command.Id == ManagedTerminalLauncher.EnhancedPowerShell7CommandId),
                "An enhanced terminal was advertised without the native Desktop executable.");
            var withoutPowerShell = Create(new RecordingProcessStarter(),
                Path.Combine(root, "missing-pwsh.exe"), desktop);
            Require(!withoutPowerShell.AdvertisedCommands().Any(command =>
                command.Id == ManagedTerminalLauncher.EnhancedPowerShell7CommandId),
                "An enhanced terminal was advertised without PowerShell 7.");

            var legacyStarts = new RecordingProcessStarter();
            var legacyLauncher = Create(legacyStarts, shell, desktop);
            var advertised = legacyLauncher.AdvertisedCommands();
            Require(advertised.Count(command => command.Id == ManagedTerminalLauncher.EnhancedPowerShell7CommandId &&
                command.DisplayName.Contains("增强终端", StringComparison.Ordinal) &&
                command.ShellType == ManagedTerminalLauncher.PowerShell7ShellType) == 1 &&
                advertised.Any(command => command.Id == ManagedTerminalLauncher.PowerShell7ShellType),
                "The remote command list lost the opt-in identity or changed the existing PowerShell 7 ID.");
            Require(legacyLauncher.ManagedShellTypeFor(ManagedTerminalLauncher.EnhancedPowerShell7CommandId) ==
                ManagedTerminalLauncher.PowerShell7ShellType,
                "The remotely advertised enhanced ID cannot resolve to the existing managed shell type.");
            var availableProfiles = legacyLauncher.AvailableProfiles();
            Require(!availableProfiles.Single(profile =>
                    profile.Id == ManagedTerminalLauncher.EnhancedPowerShell7CommandId).RunAsAdministrator &&
                availableProfiles.Single(profile =>
                    profile.Id == ManagedTerminalLauncher.PowerShell7ShellType).RunAsAdministrator,
                "The enhanced command must use the current user without changing legacy elevation.");
            using (var advertisedBody = JsonDocument.Parse(
                ProductionAgentRuntime.BuildPCUpsertBodyWithTerminalCommands(
                    "test-pc", "test", 1, new[] { ManagedTerminalLauncher.PowerShell7ShellType },
                    advertised)))
                Require(advertisedBody.RootElement.GetProperty("terminal_commands").EnumerateArray()
                    .Any(command => command.GetProperty("id").GetString() ==
                        ManagedTerminalLauncher.EnhancedPowerShell7CommandId &&
                        command.GetProperty("shell_type").GetString() ==
                        ManagedTerminalLauncher.PowerShell7ShellType),
                    "The PC upsert did not expose the launchable enhanced command ID to Android.");
            legacyLauncher.Launch(ManagedTerminalLauncher.PowerShell7ShellType, root);
            Require(legacyStarts.Starts.Count == 1 &&
                legacyStarts.Starts[0].ArgumentList.Contains("--elevate") &&
                !legacyStarts.Starts[0].ArgumentList.Contains("--broker-captures"),
                "The existing PowerShell 7 command changed from its legacy Windows Terminal launch.");

            var enhancedStarts = new RecordingProcessStarter();
            var enhancedLauncher = Create(enhancedStarts, shell, desktop);
            enhancedLauncher.Launch(ManagedTerminalLauncher.EnhancedPowerShell7CommandId, root);
            var start = enhancedStarts.Starts.Single();
            Require(start.FileName == host && !start.UseShellExecute && start.CreateNoWindow &&
                start.WindowStyle == ProcessWindowStyle.Hidden && string.IsNullOrEmpty(start.Verb) &&
                !start.ArgumentList.Contains("--elevate") &&
                start.ArgumentList.Contains("--broker-captures") &&
                start.ArgumentList.Contains("--native-display") &&
                start.ArgumentList.Contains("--terminal-state"),
                "The enhanced command launched an outer terminal/UAC or omitted its graphics Broker flags.");
            var displayIndex = start.ArgumentList.IndexOf("--native-display");
            Require(start.ArgumentList[displayIndex + 1] == desktop,
                "The enhanced command did not launch the installed native Desktop.");

            var configured = TerminalCommandProfileStore.CreateConfiguredProfile(
                ManagedTerminalLauncher.EnhancedPowerShell7CommandId, "PowerShell 7（增强终端）",
                shell, "-NoLogo", ManagedTerminalLauncher.PowerShell7ShellType,
                detected: true, enabled: true, runAsAdministrator: false);
            TerminalCommandProfileStore.SaveConfiguredProfiles(new[] { configured });
            var persisted = TerminalCommandProfileStore.LoadConfiguredProfiles().Single();
            Require(persisted.ManagedShell == ManagedTerminalLauncher.PowerShell7ShellType &&
                !persisted.RunAsAdministrator,
                "The enhanced command identity cannot survive the existing Windows settings flow.");
            File.Delete(desktop);
            try
            {
                enhancedLauncher.Launch(ManagedTerminalLauncher.EnhancedPowerShell7CommandId, root);
                throw new InvalidOperationException("A stale enhanced command silently fell back to legacy.");
            }
            catch (FileOperationException error) when (error.Code == "NATIVE_DISPLAY_NOT_FOUND") { }
        }
        finally
        {
            AppContext.SetData(TerminalCommandProfileStore.StoragePathAppContextKey, previousSettings);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}

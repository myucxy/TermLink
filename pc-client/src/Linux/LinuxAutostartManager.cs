using System.Security;
using System.Text;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Linux;

public sealed record LinuxAutostartOptions(
    string AgentExecutable,
    IReadOnlyList<string> AgentArguments,
    string ConfigurationHome,
    string UnitName = "termlink-agent.service");

public sealed class LinuxAutostartManager : IAutostartManager
{
    private readonly LinuxAutostartOptions options;
    private readonly ICommandRunner runner;
    private readonly string? systemctl;

    public LinuxAutostartManager(LinuxAutostartOptions options, ICommandRunner? runner = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (!Path.IsPathRooted(options.AgentExecutable) || !Path.IsPathRooted(options.ConfigurationHome) ||
            !options.UnitName.EndsWith(".service", StringComparison.Ordinal))
            throw new ArgumentException("Linux autostart paths are invalid.", nameof(options));
        this.runner = runner ?? new SystemCommandRunner();
        systemctl = ExecutableLocator.Find("systemctl");
    }

    public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(UnitPath) || File.Exists(DesktopPath));
    }

    public async ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            if (await CanUseSystemdAsync(cancellationToken).ConfigureAwait(false))
            {
                AtomicWrite(UnitPath, BuildSystemdUnit(options));
                var reload = await runner.RunAsync(systemctl!, ["--user", "daemon-reload"], null, cancellationToken).ConfigureAwait(false);
                RequireSuccess(reload, "systemd daemon-reload");
                var start = await runner.RunAsync(systemctl!, ["--user", "enable", "--now", options.UnitName], null, cancellationToken).ConfigureAwait(false);
                RequireSuccess(start, "systemd enable");
                if (File.Exists(DesktopPath)) File.Delete(DesktopPath);
            }
            else
            {
                AtomicWrite(DesktopPath, BuildDesktopEntry(options));
            }
            return;
        }

        if (systemctl is not null && File.Exists(UnitPath))
            await runner.RunAsync(systemctl, ["--user", "disable", "--now", options.UnitName], null, cancellationToken).ConfigureAwait(false);
        if (File.Exists(UnitPath)) File.Delete(UnitPath);
        if (File.Exists(DesktopPath)) File.Delete(DesktopPath);
        if (systemctl is not null)
            await runner.RunAsync(systemctl, ["--user", "daemon-reload"], null, cancellationToken).ConfigureAwait(false);
    }

    public static string BuildSystemdUnit(LinuxAutostartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var command = string.Join(' ', new[] { options.AgentExecutable }.Concat(options.AgentArguments).Select(SystemdQuote));
        return "[Unit]\nDescription=TermLink independent Agent\nAfter=network-online.target\n\n" +
            "[Service]\nType=simple\nExecStart=" + command + "\nRestart=on-failure\nRestartSec=3\n" +
            "NoNewPrivileges=true\nPrivateTmp=true\nProtectSystem=strict\nProtectHome=read-only\n" +
            "ReadWritePaths=%h/.local/share/TermLink %t/termlink\n\n" +
            "[Install]\nWantedBy=default.target\n";
    }

    public static string BuildDesktopEntry(LinuxAutostartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var command = string.Join(' ', new[] { options.AgentExecutable }.Concat(options.AgentArguments).Select(DesktopQuote));
        return "[Desktop Entry]\nType=Application\nName=TermLink Agent\nNoDisplay=true\n" +
            "X-GNOME-Autostart-enabled=true\nExec=" + command + "\n";
    }

    private string UnitPath => Path.Combine(options.ConfigurationHome, "systemd", "user", options.UnitName);
    private string DesktopPath => Path.Combine(options.ConfigurationHome, "autostart", "termlink-agent.desktop");

    private async ValueTask<bool> CanUseSystemdAsync(CancellationToken cancellationToken)
    {
        if (systemctl is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"))) return false;
        var result = await runner.RunAsync(systemctl, ["--user", "is-system-running"], null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 || result.StandardOutput.Trim() == "degraded";
    }

    private static string SystemdQuote(string value)
    {
        if (value.IndexOfAny(['\0', '\n', '\r']) >= 0) throw new InvalidDataException("An autostart argument is invalid.");
        return '"' + value.Replace("%", "%%", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }

    private static string DesktopQuote(string value)
    {
        if (value.IndexOfAny(['\0', '\n', '\r']) >= 0) throw new InvalidDataException("An autostart argument is invalid.");
        return '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Autostart directory is missing.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".new";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }

    private static void RequireSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
    }
}

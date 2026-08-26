using System.Security;
using System.Text;
using System.Xml;
using TermLink.Platform;

namespace TermLink.Platform.MacOS;

public sealed record MacLaunchdOptions(
    string Label,
    string AgentExecutable,
    IReadOnlyList<string> Arguments,
    string LaunchAgentsDirectory,
    uint UserId);

public sealed class MacLaunchdAutostartManager : IAutostartManager
{
    private readonly MacLaunchdOptions options;
    private readonly IMacCommandRunner runner;

    public MacLaunchdAutostartManager(MacLaunchdOptions options, IMacCommandRunner? runner = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (!Path.IsPathRooted(options.AgentExecutable) || !Path.IsPathRooted(options.LaunchAgentsDirectory) ||
            string.IsNullOrWhiteSpace(options.Label) || options.Label.IndexOfAny(['\0', '/', '\\']) >= 0)
            throw new ArgumentException("launchd options are invalid.", nameof(options));
        this.runner = runner ?? new MacCommandRunner();
    }

    public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(PlistPath));
    }

    public async ValueTask SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("launchd requires macOS.");
        var domain = $"gui/{options.UserId}";
        if (enabled)
        {
            Directory.CreateDirectory(options.LaunchAgentsDirectory);
            AtomicWrite(PlistPath, BuildPlist(options));
            var bootstrap = await runner.RunAsync("/bin/launchctl", ["bootstrap", domain, PlistPath], cancellationToken).ConfigureAwait(false);
            if (bootstrap.ExitCode != 0 && !bootstrap.StandardError.Contains("already", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"launchd bootstrap failed: {bootstrap.StandardError.Trim()}");
            var kick = await runner.RunAsync("/bin/launchctl", ["kickstart", "-k", $"{domain}/{options.Label}"], cancellationToken).ConfigureAwait(false);
            if (kick.ExitCode != 0) throw new InvalidOperationException($"launchd kickstart failed: {kick.StandardError.Trim()}");
        }
        else
        {
            var bootout = await runner.RunAsync("/bin/launchctl", ["bootout", domain, PlistPath], cancellationToken).ConfigureAwait(false);
            if (bootout.ExitCode != 0 && !bootout.StandardError.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"launchd bootout failed: {bootout.StandardError.Trim()}");
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
        }
    }

    public static string BuildPlist(MacLaunchdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
        var arguments = new StringBuilder();
        arguments.Append("    <array>\n      <string>").Append(Escape(options.AgentExecutable)).Append("</string>\n");
        foreach (var argument in options.Arguments)
        {
            if (argument.IndexOf('\0') >= 0) throw new InvalidDataException("A launchd argument contains NUL.");
            arguments.Append("      <string>").Append(Escape(argument)).Append("</string>\n");
        }
        arguments.Append("    </array>\n");
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n  <dict>\n" +
            "    <key>Label</key><string>" + Escape(options.Label) + "</string>\n" +
            "    <key>ProgramArguments</key>\n" + arguments +
            "    <key>RunAtLoad</key><true/>\n    <key>KeepAlive</key>\n" +
            "    <dict><key>SuccessfulExit</key><false/></dict>\n" +
            "    <key>ProcessType</key><string>Background</string>\n" +
            "    <key>ThrottleInterval</key><integer>3</integer>\n" +
            "  </dict>\n</plist>\n";
    }

    private string PlistPath => Path.Combine(options.LaunchAgentsDirectory, options.Label + ".plist");

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }
}

using TermLink.Pc.Contracts;
using TermLink.Platform;

namespace TermLink.Platform.Unix;

public static class ExecutableLocator
{
    public static string? Find(string executable, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOf('\0') >= 0)
            throw new ArgumentException("An executable name is required.", nameof(executable));
        if (Path.IsPathRooted(executable)) return IsExecutable(executable) ? Path.GetFullPath(executable) : null;
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return null;
        path ??= Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executable));
                if (IsExecutable(candidate)) return candidate;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        return null;
    }

    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode executable = UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & executable) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class PosixTerminalCommandDiscovery : ITerminalCommandDiscovery
{
    private static readonly (string Id, string Name, string Executable, string[] Arguments)[] KnownCommands =
    [
        ("bash", "Bash", "bash", ["-l"]),
        ("zsh", "Z shell", "zsh", ["-l"]),
        ("fish", "fish", "fish", ["-l"]),
        ("pwsh", "PowerShell", "pwsh", ["-NoLogo", "-NoProfile"]),
        ("sh", "POSIX shell", "sh", []),
    ];

    public ValueTask<IReadOnlyList<TerminalCommandCapability>> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<TerminalCommandCapability>(KnownCommands.Length);
        foreach (var command in KnownCommands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ExecutableLocator.Find(command.Executable);
            if (path is null) continue;
            result.Add(new TerminalCommandCapability(
                command.Id,
                command.Name,
                path,
                command.Arguments,
                true));
        }
        return ValueTask.FromResult<IReadOnlyList<TerminalCommandCapability>>(result);
    }

    internal static (string Executable, IReadOnlyList<string> Arguments) Resolve(string commandId)
    {
        var known = KnownCommands.FirstOrDefault(value =>
            string.Equals(value.Id, commandId, StringComparison.Ordinal));
        if (known == default)
            throw new InvalidDataException("The requested terminal command is not supported.");
        var executable = ExecutableLocator.Find(known.Executable) ??
            throw new FileNotFoundException($"The requested terminal command '{commandId}' is not installed.");
        return (executable, known.Arguments);
    }
}

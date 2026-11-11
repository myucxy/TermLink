// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text;

namespace TermLink.Platform.Unix;

/// <summary>
/// An absolute executable and literal interpreter arguments resolved without a
/// shell command string. Probes and their later app-server launch share it.
/// </summary>
public sealed class PosixCliLaunchDescriptor
{
    public string CliPath { get; }
    public string ExecutablePath { get; }
    public IReadOnlyList<string> PrefixArguments { get; }
    public bool NativeAgentCompatible { get; }

    private PosixCliLaunchDescriptor(string cliPath, string executablePath, string[] prefix, bool compatible)
    {
        CliPath = cliPath;
        ExecutablePath = executablePath;
        PrefixArguments = Array.AsReadOnly(prefix);
        NativeAgentCompatible = compatible;
    }

    public static PosixCliLaunchDescriptor Resolve(string path, Func<string, string?>? find = null)
    {
        find ??= name => ExecutableLocator.Find(name);
        if (!Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0 || !ExecutableLocator.IsExecutable(path))
            throw new IOException("AI_EXECUTABLE_INVALID");
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[1024];
        var count = file.Read(header);
        if (count < 2 || header[0] != '#' || header[1] != '!') return new(path, path, [], true);
        var end = header[..count].IndexOf((byte)'\n');
        if (end < 0 && count == header.Length) throw new UnsupportedShimException();
        if (end < 0) end = count;
        var parts = Encoding.UTF8.GetString(header[2..end]).Trim()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Path.IsPathFullyQualified(parts[0])) throw new UnsupportedShimException();
        var runtimeIndex = 0;
        if (parts[0] == "/usr/bin/env")
        {
            runtimeIndex = parts.Length > 1 && parts[1] == "-S" ? 2 : 1;
            if (parts.Length <= runtimeIndex || parts[runtimeIndex].StartsWith('-') ||
                parts[runtimeIndex].Contains('=') || parts.Any(part => part.IndexOfAny(['\'', '"', '\\']) >= 0))
                throw new UnsupportedShimException();
        }
        var runtimeName = parts[runtimeIndex];
        var runtime = runtimeIndex == 0 || Path.IsPathFullyQualified(runtimeName) ? runtimeName : find(runtimeName);
        if (runtime is null || !Path.IsPathFullyQualified(runtime) || !ExecutableLocator.IsExecutable(runtime))
            throw new MissingRuntimeException();
        var prefix = new List<string>();
        var options = parts.Skip(runtimeIndex + 1).ToArray();
        var interpreter = Path.GetFileName(runtime);
        if (interpreter is "sh" or "bash" or "dash" or "zsh" or "ksh" or "fish" or "csh" or "tcsh")
        {
            if (options.Any(option => option is not ("-e" or "-u" or "-f" or "-eu" or "-ue")))
                throw new UnsupportedShimException();
            if (interpreter == "bash") { prefix.Add("--noprofile"); prefix.Add("--norc"); }
            else if (interpreter is "zsh" or "csh" or "tcsh") prefix.Add("-f");
            else if (interpreter == "fish") prefix.Add("--no-config");
        }
        var publicRuntime = Path.GetFileName(runtimeName) is "node" or "nodejs" or "bun";
        if (publicRuntime && options.Any(option => option is not ("--no-warnings" or "--no-deprecation" or "--enable-source-maps")))
            throw new UnsupportedShimException();
        prefix.AddRange(options);
        prefix.Add(path);
        // Shell/Python/custom interpreter shims can still be checked as local
        // installations, but do not become remote native AI launchers.
        return new(path, runtime, prefix.ToArray(), publicRuntime);
    }

    public ProcessStartInfo CreateStartInfo(string directory, IReadOnlyList<string> arguments, bool disableGrokUpdater = false)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0') ||
            arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("AI_LAUNCH_ARGUMENT_INVALID");
        if (!ExecutableLocator.IsExecutable(CliPath) || !ExecutableLocator.IsExecutable(ExecutablePath))
            throw new IOException("AI_EXECUTABLE_CHANGED");
        var start = new ProcessStartInfo
        {
            FileName = ExecutablePath, WorkingDirectory = directory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PATH"] = ExecutableLocator.SafeSearchPath();
        foreach (var name in new[] { "BASH_ENV", "ENV", "ZDOTDIR", "NODE_OPTIONS", "NODE_PATH", "BUN_OPTIONS", "DYLD_INSERT_LIBRARIES" })
            start.Environment.Remove(name);
        if (disableGrokUpdater) start.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
        foreach (var argument in PrefixArguments.Concat(arguments)) start.ArgumentList.Add(argument);
        return start;
    }

    internal sealed class MissingRuntimeException : IOException;
    internal sealed class UnsupportedShimException : IOException;
}

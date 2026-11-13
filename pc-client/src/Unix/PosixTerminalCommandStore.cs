// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TermLink.Pc.Contracts;

namespace TermLink.Platform.Unix;

/// <summary>A local launch directory. Discovery validates data, never runs commands.</summary>
public sealed class PosixTerminalCommandStore
{
    private const int MaximumBytes = 48 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    private readonly string path;
    private readonly SemaphoreSlim gate;
    private readonly Func<IReadOnlyList<TerminalCommandCapability>> detectBuiltIns;

    public PosixTerminalCommandStore(string storagePath, Func<IReadOnlyList<TerminalCommandCapability>>? detectBuiltIns = null)
    {
        if (string.IsNullOrWhiteSpace(storagePath) || !Path.IsPathFullyQualified(storagePath) || storagePath.Contains('\0'))
            throw new ArgumentException("An absolute command storage path is required.", nameof(storagePath));
        path = Path.GetFullPath(storagePath);
        gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        this.detectBuiltIns = detectBuiltIns ?? PosixTerminalCommandDiscovery.DetectBuiltIns;
    }

    /// <summary>Includes disabled or temporarily missing configured entries for local editing.</summary>
    public async ValueTask<IReadOnlyList<TerminalCommandCapability>> DiscoverAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); return DiscoverCore(); }
        finally { gate.Release(); }
    }

    public async ValueTask SaveAsync(IReadOnlyList<TerminalCommandCapability> commands, CancellationToken token)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())) throw new PlatformNotSupportedException();
        var normalized = Validate(commands);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var ids = normalized.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
            // A stale UI snapshot must not silently re-enable a hidden automatic shell.
            normalized.AddRange(Read().Where(command => command.Detected && !command.Enabled && !ids.Contains(command.Id)));
            if (normalized.Count + Validate(detectBuiltIns()).Count(command => !normalized.Any(item => item.Id == command.Id)) >
                PcContractLimits.MaximumTerminalCommands) throw Invalid();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(1, normalized), Json);
            if (bytes.Length > MaximumBytes) throw Invalid();
            EnsureDirectory(); RejectLink(path);
            var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, UnixCreateMode = PrivateFile, Options = FileOptions.WriteThrough }))
                { file.Write(bytes); file.Flush(flushToDisk: true); }
                token.ThrowIfCancellationRequested(); ValidateAncestors(Path.GetDirectoryName(path)!); RejectLink(path);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Resolve again at launch. Missing/disabled/unknown IDs never fall back to another command.</summary>
    public (string Executable, IReadOnlyList<string> Arguments) Resolve(string commandId)
    {
        gate.Wait();
        try
        {
            var command = DiscoverCore().SingleOrDefault(item => string.Equals(item.Id, commandId, StringComparison.Ordinal));
            if (command is null || !command.Enabled || !ExecutableLocator.IsExecutable(command.Executable))
                throw new InvalidDataException("TERMINAL_COMMAND_UNAVAILABLE");
            return (command.Executable, Array.AsReadOnly(command.Arguments.ToArray()));
        }
        finally { gate.Release(); }
    }

    private IReadOnlyList<TerminalCommandCapability> DiscoverCore()
    {
        var configured = Read();
        var ids = configured.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        var result = Validate(detectBuiltIns()).Where(command => !ids.Contains(command.Id)).Concat(configured).ToArray();
        if (result.Length > PcContractLimits.MaximumTerminalCommands) throw Invalid();
        return Array.AsReadOnly(result);
    }

    private List<TerminalCommandCapability> Read()
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())) throw new PlatformNotSupportedException();
        ValidateAncestors(Path.GetDirectoryName(path)!); RejectLink(path);
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumBytes || (File.GetUnixFileMode(path) & PublicPermissions) != 0) throw Invalid();
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaximumBytes) throw Invalid();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateMembers(document.RootElement);
            var stored = document.RootElement.Deserialize<Stored>(Json);
            if (stored is null || stored.Schema != 1 || stored.Commands is null) throw Invalid();
            return Validate(stored.Commands);
        }
        catch (JsonException error) { throw new InvalidDataException("TERMINAL_COMMAND_STORE_INVALID", error); }
    }

    private static List<TerminalCommandCapability> Validate(IReadOnlyList<TerminalCommandCapability> commands)
    {
        if (commands is null || commands.Count > PcContractLimits.MaximumTerminalCommands) throw Invalid();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TerminalCommandCapability>();
        foreach (var command in commands)
        {
            var normalized = PosixTerminalCommandRules.Validate(command);
            if (!ids.Add(normalized.Id)) throw Invalid();
            result.Add(normalized);
        }
        return result;
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())) throw new PlatformNotSupportedException();
        var directory = Path.GetDirectoryName(path)!;
        ValidateAncestors(directory);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateAncestors(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void ValidateAncestors(string directory)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())) throw new PlatformNotSupportedException();
        // macOS system aliases are canonical OS paths, not configurable links.
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (directory == alias || directory.StartsWith(alias + "/", StringComparison.Ordinal)) { directory = "/private" + directory; break; }
        for (var current = directory; !string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current); current = Path.GetDirectoryName(current))
            RejectLink(current);
    }
    private static void RejectLink(string candidate)
    {
        var info = new FileInfo(candidate);
        if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("TERMINAL_COMMAND_STORE_LINK_REJECTED");
    }
    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicateMembers(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }
    private sealed record Stored(int Schema, IReadOnlyList<TerminalCommandCapability> Commands);
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PublicPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static InvalidDataException Invalid() => new("TERMINAL_COMMAND_STORE_INVALID");
}

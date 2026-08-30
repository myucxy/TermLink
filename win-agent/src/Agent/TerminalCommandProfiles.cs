using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermLink.Agent
{
    internal sealed class TerminalCommandProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; }

        [JsonPropertyName("executable_path")]
        public string ExecutablePath { get; set; }

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }

        [JsonPropertyName("managed_shell")]
        public string ManagedShell { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("run_as_administrator")]
        public bool RunAsAdministrator { get; set; } = true;
    }

    internal sealed class TerminalCommandCapability
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; }

        [JsonPropertyName("shell_type")]
        public string ShellType { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; }
    }

    /// <summary>
    /// Stores only locally-authored launch details. The server receives the
    /// corresponding capability descriptors, never executable paths or arguments.
    /// </summary>
    internal static class TerminalCommandProfileStore
    {
        internal const string StoragePathAppContextKey = "TermLink.TerminalCommands.StoragePath";
        internal const int MaximumManualProfiles = 20;
        internal const int MaximumDisplayNameBytes = 80;
        internal const int MaximumArgumentsLength = 2048;
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
        };

        internal static IReadOnlyList<TerminalCommandProfile> LoadManualProfiles()
        {
            return LoadConfiguredProfiles()
                .Where(profile => string.Equals(profile.Source, "manual", StringComparison.Ordinal))
                .ToArray();
        }

        internal static IReadOnlyList<TerminalCommandProfile> LoadConfiguredProfiles()
        {
            lock (Gate)
            {
                var path = ResolveStoragePath();
                if (!File.Exists(path)) return Array.Empty<TerminalCommandProfile>();
                try
                {
                    var stored = JsonSerializer.Deserialize<List<TerminalCommandProfile>>(
                        File.ReadAllText(path, Encoding.UTF8),
                        JsonOptions) ?? new List<TerminalCommandProfile>();
                    return NormalizeProfiles(stored, requireExecutable: false);
                }
                catch (Exception error) when (
                    error is IOException || error is UnauthorizedAccessException || error is JsonException ||
                    error is InvalidDataException || error is ArgumentException || error is NotSupportedException)
                {
                    return Array.Empty<TerminalCommandProfile>();
                }
            }
        }

        internal static void SaveManualProfiles(IEnumerable<TerminalCommandProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            var detected = LoadConfiguredProfiles()
                .Where(profile => string.Equals(profile.Source, "detected", StringComparison.Ordinal));
            SaveConfiguredProfiles(detected.Concat(profiles));
        }

        internal static void SaveConfiguredProfiles(IEnumerable<TerminalCommandProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            var normalized = NormalizeProfiles(profiles, requireExecutable: true);
            lock (Gate)
            {
                var path = ResolveStoragePath();
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("The terminal-command settings path is invalid.");
                Directory.CreateDirectory(directory);
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(
                        temporary,
                        JsonSerializer.Serialize(normalized, JsonOptions),
                        new UTF8Encoding(false));
                    File.Move(temporary, path, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        internal static TerminalCommandProfile CreateManualProfile(
            string id,
            string displayName,
            string executable,
            string arguments,
            bool enabled = true,
            bool runAsAdministrator = true)
        {
            var resolvedExecutable = ResolveExecutable(executable);
            return NormalizeProfile(new TerminalCommandProfile
            {
                Id = string.IsNullOrWhiteSpace(id) ? "custom-" + Guid.NewGuid().ToString("N") : id,
                DisplayName = displayName,
                ExecutablePath = resolvedExecutable,
                Arguments = arguments,
                ManagedShell = ManagedTerminalLauncher.CustomShellType,
                Source = "manual",
                Enabled = enabled,
                RunAsAdministrator = true
            }, requireExecutable: true);
        }

        internal static TerminalCommandProfile CreateConfiguredProfile(
            string id,
            string displayName,
            string executable,
            string arguments,
            string managedShell,
            bool detected,
            bool enabled,
            bool runAsAdministrator)
        {
            var resolvedExecutable = ResolveExecutable(executable);
            return NormalizeProfile(new TerminalCommandProfile
            {
                Id = id,
                DisplayName = displayName,
                ExecutablePath = resolvedExecutable,
                Arguments = arguments,
                ManagedShell = managedShell,
                Source = detected ? "detected" : "manual",
                Enabled = enabled,
                RunAsAdministrator = true
            }, requireExecutable: true);
        }

        internal static string ResolveExecutable(string value)
        {
            var input = (value ?? string.Empty).Trim().Trim('"');
            if (input.Length == 0) throw new ArgumentException("An executable is required.", nameof(value));
            if (Path.IsPathFullyQualified(input))
            {
                var fullPath = Path.GetFullPath(input);
                if (!File.Exists(fullPath)) throw new FileNotFoundException("The command executable was not found.", fullPath);
                return fullPath;
            }
            if (input.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
                throw new ArgumentException("The command path must be absolute or a file name on PATH.", nameof(value));

            var extensions = Path.HasExtension(input)
                ? new[] { string.Empty }
                : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.CMD;.BAT")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var extension in extensions)
                {
                    try
                    {
                        var candidate = Path.Combine(directory.Trim().Trim('"'), input + extension);
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    }
                    catch (Exception error) when (
                        error is ArgumentException || error is NotSupportedException || error is PathTooLongException)
                    {
                    }
                }
            }
            throw new FileNotFoundException("The command executable was not found on PATH.", input);
        }

        private static IReadOnlyList<TerminalCommandProfile> NormalizeProfiles(
            IEnumerable<TerminalCommandProfile> profiles,
            bool requireExecutable)
        {
            var result = new List<TerminalCommandProfile>();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles)
            {
                if (result.Count >= 24)
                    throw new InvalidDataException("Too many terminal commands are configured.");
                var normalized = NormalizeProfile(profile, requireExecutable);
                if (!identifiers.Add(normalized.Id))
                    throw new InvalidDataException("Terminal command identifiers must be unique.");
                result.Add(normalized);
            }
            return result;
        }

        private static TerminalCommandProfile NormalizeProfile(
            TerminalCommandProfile profile,
            bool requireExecutable)
        {
            if (profile == null) throw new InvalidDataException("A terminal command is missing.");
            var id = (profile.Id ?? string.Empty).Trim().ToLowerInvariant();
            var detected = string.Equals(profile.Source, "detected", StringComparison.Ordinal);
            if (!ValidIdentifier(id) || detected != ManagedTerminalLauncher.IsBuiltInShellType(id))
                throw new InvalidDataException("The terminal command identifier or source is invalid.");
            var displayName = (profile.DisplayName ?? string.Empty).Trim();
            if (displayName.Length == 0 || Encoding.UTF8.GetByteCount(displayName) > MaximumDisplayNameBytes ||
                displayName.Any(char.IsControl))
                throw new InvalidDataException("The terminal command display name is invalid.");
            var arguments = (profile.Arguments ?? string.Empty).Trim();
            if (arguments.Length > MaximumArgumentsLength || arguments.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new InvalidDataException("The terminal command arguments are invalid.");
            var executable = (profile.ExecutablePath ?? string.Empty).Trim();
            if (executable.Length == 0 || !Path.IsPathFullyQualified(executable))
                throw new InvalidDataException("The terminal command executable path is invalid.");
            executable = Path.GetFullPath(executable);
            if (requireExecutable && profile.Enabled && !File.Exists(executable))
                throw new FileNotFoundException("The terminal command executable was not found.", executable);
            var managedShell = detected ? id : ManagedTerminalLauncher.CustomShellType;
            if (detected && !string.Equals(profile.ManagedShell, managedShell, StringComparison.Ordinal))
                throw new InvalidDataException("The detected terminal command shell is invalid.");
            return new TerminalCommandProfile
            {
                Id = id,
                DisplayName = displayName,
                ExecutablePath = executable,
                Arguments = arguments,
                ManagedShell = managedShell,
                Source = detected ? "detected" : "manual",
                Enabled = profile.Enabled,
                // Administrator launch is a product invariant. Keep the serialized
                // field for compatibility with older clients, but ignore a legacy
                // false value when loading or saving command configuration.
                RunAsAdministrator = true
            };
        }

        private static bool ValidIdentifier(string value)
        {
            if (value.Length < 1 || value.Length > 64 || value[0] < 'a' || value[0] > 'z') return false;
            return value.All(character =>
                character >= 'a' && character <= 'z' ||
                character >= '0' && character <= '9' ||
                character == '.' || character == '_' || character == '-');
        }

        private static string ResolveStoragePath()
        {
            var overridePath = AppContext.GetData(StoragePathAppContextKey) as string;
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
            var localData = Environment.GetEnvironmentVariable(
                "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(localData))
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TERMLINK_ACCEPTANCE_CA_FILE")) ||
                    !Path.IsPathRooted(localData))
                {
                    throw new InvalidOperationException(
                        "The acceptance terminal-command root is not authorized.");
                }
                localData = Path.GetFullPath(localData);
            }
            else
            {
                localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            return Path.Combine(
                localData,
                "TermLink",
                "Agent",
                "terminal-commands.json");
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TermLink.Agent;
using TermLink.Ai;

/// <summary>
/// Optional, real installed-runtime validation. No user turn is ever submitted.
/// Run only through the sandboxed parent entry point, independently of fixtures.
/// </summary>
internal static class RealZcodeValidation
{
    private const string SandboxRoot = "TERMLINK_ZCODE_VALIDATION_SANDBOX_ROOT";
    private const int MaximumOutputCharacters = 1024 * 1024;
    private const string FixtureProvider = "termlink-offline-validation";
    private const string FixtureModel = "termlink-no-send-model";

    internal static async Task RunSandboxedAsync(string directory)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("NOT APPLICABLE real ZCode installed runtime (macOS only)");
            return;
        }
        Require(Path.IsPathFullyQualified(directory), "A fully qualified validation directory is required.");
        var root = Path.Combine(Path.GetFullPath(directory), "real-zcode-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(root);
        var canary = Path.Combine(root, "sandbox-denied-canary.txt");
        await File.WriteAllTextAsync(canary, "nonsecret sandbox validation canary");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Require(Path.IsPathFullyQualified(userProfile), "A user profile is required to build the deny policy.");
        var denied = new[]
        {
            Path.Combine(userProfile, ".zcode"),
            Path.Combine(userProfile, "Library", "Application Support", "ZCode"),
            Path.Combine(userProfile, "Library", "Application Support", "zcode"),
        };
        Require(!denied.Any(path => root == path || root.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "Validation storage must not be inside an existing ZCode profile.");
        var profile = "(version 1)\n(allow default)\n" +
            "(deny network*)\n(allow network* (local unix-socket))\n(allow network* (remote unix-socket))\n" +
            string.Concat(denied.Select(path => "(deny file-read* file-write* (subpath " + PolicyQuote(path) + "))\n")) +
            "(deny file-read* file-write* (literal " + PolicyQuote(canary) + "))\n";
        var profilePath = Path.Combine(root, "isolation.sb");
        await File.WriteAllTextAsync(profilePath, profile);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Validation executable is unavailable.");
        var start = new ProcessStartInfo("/usr/bin/sandbox-exec")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add("-f"); start.ArgumentList.Add(profilePath); start.ArgumentList.Add(executable);
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--real-isolated"); start.ArgumentList.Add(root);
        // Remove inherited provider/key variables without examining their values.
        foreach (var key in start.Environment.Keys.Where(IsSensitiveEnvironmentName).ToArray()) start.Environment.Remove(key);
        start.Environment[SandboxRoot] = root;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new IOException("Sandboxed validation did not start.");
        using var canceled = timeout.Token.Register(() => Kill(process));
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumOutputCharacters, timeout.Token);
        try
        {
            process.StandardInput.Close();
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            // Only messages authored by this validator are forwarded. Raw child
            // stderr, which may contain runtime configuration details, is discarded.
            foreach (var line in (await stdout).Split('\n').Where(line => line.StartsWith("PASS real ZCode ", StringComparison.Ordinal) ||
                line.StartsWith("UNVERIFIED real ZCode ", StringComparison.Ordinal))) Console.WriteLine(line);
            Require(process.ExitCode == 0, "Sandboxed real ZCode validation failed; inspect its sanitized report.");
            Require(File.Exists(Path.Combine(root, "real-zcode-report.json")), "The isolated runtime did not write its report.");
            Console.WriteLine("PASS real ZCode sandbox runner: isolated configuration/session storage, denied existing profiles and IP networking");
        }
        finally
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
        }
    }

    internal static async Task RunAsync(string directory)
    {
        Require(OperatingSystem.IsMacOS(), "Real ZCode validation requires macOS.");
        var root = Path.GetFullPath(directory);
        Require(Path.IsPathFullyQualified(directory) && Environment.GetEnvironmentVariable(SandboxRoot) == root,
            "Real runtime validation must run through its sandbox entry point.");
        var canaryBlocked = false;
        try { using var ignored = File.OpenRead(Path.Combine(root, "sandbox-denied-canary.txt")); }
        catch (UnauthorizedAccessException) { canaryBlocked = true; }
        Require(canaryBlocked, "The sandbox file-denial canary was readable; refusing to start ZCode.");
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await RequireIpNetworkDeniedAsync(total.Token);
        var results = new List<Check> { new("sandbox/canary", "passed"), new("sandbox/ip-canary", "passed") };
        var report = Path.Combine(root, "real-zcode-report.json");
        var normalizedVersion = "";
        try
        {
            // Help/version discovery runs inside the same deny policy. No real
            // profile can be opened even if discovery asks for its default path.
            var detected = await ZcodeDesktopRuntime.DetectMacAsync(total.Token);
            normalizedVersion = Regex.Match(detected.Version ?? "", @"(?<![0-9])([0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5})(?![0-9])").Value;
            results.Add(new("installed/help-version", detected.Status));
            Require(detected.Id == "zcode" && detected.Strategy == "native", "Installed discovery returned an unexpected strategy.");
            if (detected.Status != "available")
            {
                Console.WriteLine("UNVERIFIED real ZCode installed runtime: " + SafeCode(detected.Status));
                return;
            }
            var raw = Isolate(detected, root, "raw");
            var workspace = Path.Combine(root, "workspace"); CreatePrivateDirectory(workspace);
            var needsConfigurationFallback = false;
            var useFixtureConfiguration = false;
            await using (var rpc = new RuntimeRpc(raw, workspace, total.Token))
            {
                var capabilities = await rpc.CallAsync("runtime/capabilities", new { });
                Require(capabilities.Success, "Installed runtime rejected runtime/capabilities.");
                results.Add(new("runtime/capabilities", "passed"));
                var created = await rpc.CallAsync("session/create", new
                {
                    workspace = new { workspaceKey = workspace, workspacePath = workspace }, mode = "build",
                });
                if (!created.Success)
                {
                    results.Add(new("session/create", "unverified", created.ErrorCode));
                    Console.WriteLine("UNVERIFIED real ZCode session/create without CLI model configuration: " + created.ErrorCode);
                    needsConfigurationFallback = created.MissingConfiguration;
                }
                else await VerifyCreatedSessionAsync(rpc, created.Value, results);
                Require(rpc.SubmittedMethods.All(method => method is "runtime/capabilities" or "session/create" or "session/subscribe" or "session/read" or "session/stop"),
                    "A real-runtime user turn or unrelated operation was submitted.");
            }
            results.Add(new("raw/process-exit", "passed"));
            if (needsConfigurationFallback)
            {
                // The empty profile's own error selected this branch. This is
                // an offline personal API fixture, never an account provider or
                // desktop-authorized model. No user turn is submitted to it.
                var fallback = Isolate(detected, root, "configured-raw");
                await WriteOfflineProviderAsync(fallback, total.Token);
                results.Add(new("isolated/provider-schema-1", "passed"));
                await using (var rpc = new RuntimeRpc(fallback, workspace, total.Token))
                {
                    Require((await rpc.CallAsync("runtime/capabilities", new { })).Success, "Configured runtime rejected capabilities.");
                    var created = await rpc.CallAsync("session/create", new
                    {
                        workspace = new { workspaceKey = workspace, workspacePath = workspace }, mode = "build",
                        model = new { modelId = FixtureModel, providerId = FixtureProvider },
                    });
                    Require(created.Success, "The isolated offline provider did not permit session creation.");
                    await VerifyCreatedSessionAsync(rpc, created.Value, results);
                }
                useFixtureConfiguration = true;
                results.Add(new("configured-raw/process-exit", "passed"));
            }
            var owner = Guid.NewGuid();
            var adapterAgent = Isolate(detected, root, "adapter");
            if (useFixtureConfiguration) await WriteOfflineProviderAsync(adapterAgent, total.Token);
            ValidateIsolation(AiAgentDiscovery.StartInfo(adapterAgent, workspace, "app-server"), adapterAgent);
            Process? adapterProcess = null;
            using (var adapter = new AiAgentSession(adapterAgent, workspace, owner,
                useFixtureConfiguration ? FixtureModel : "", useFixtureConfiguration ? FixtureProvider : ""))
            {
                try
                {
                    await adapter.StartAsync().WaitAsync(TimeSpan.FromSeconds(20), total.Token);
                    adapterProcess = SessionProcess(adapter);
                    var snapshot = JsonSerializer.SerializeToElement(adapter.Snapshot(owner, true), AiSessionManager.Json);
                    Require(Text(snapshot, "state") == "idle" && Property(snapshot, "messages").GetArrayLength() == 0 &&
                        Property(snapshot, "pending").GetArrayLength() == 0, "The shared adapter did not reach idle without a turn.");
                    results.Add(new("shared-adapter/start", "passed"));
                    await adapter.ApplyAsync(owner, new("end", OperationId: Guid.NewGuid().ToString("D"), ControlVersion: 1))
                        .WaitAsync(TimeSpan.FromSeconds(8), total.Token);
                    Require(!adapter.IsAlive, "The shared adapter stayed alive after end.");
                    results.Add(new("shared-adapter/end", "passed"));
                }
                catch (AiOperationException error)
                {
                    adapterProcess = SessionProcess(adapter);
                    if (useFixtureConfiguration) throw;
                    results.Add(new("shared-adapter/start", "unverified", SafeCode(error.Code)));
                    Console.WriteLine("UNVERIFIED real ZCode shared adapter without CLI model configuration: " + SafeCode(error.Code));
                }
            }
            if (adapterProcess is not null)
            {
                await adapterProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5), total.Token);
                Require(adapterProcess.HasExited, "The shared adapter left its runtime process alive.");
                results.Add(new("shared-adapter/process-exit", "passed"));
            }
            Console.WriteLine("PASS real ZCode installed runtime: capabilities checked; no session/send, tools or model request submitted");
        }
        catch (Exception error)
        {
            results.Add(new("validation", "failed", SafeCode(error.GetType().Name)));
            throw new InvalidOperationException("Isolated real ZCode validation failed: " + SafeCode(error.GetType().Name));
        }
        finally
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                kind = "real-installed-runtime", installedVersion = normalizedVersion, modelRequestsSubmitted = 0, existingProfilesDenied = true,
                ipNetworkingDenied = true, checks = results,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task VerifyCreatedSessionAsync(RuntimeRpc rpc, JsonElement created, List<Check> results)
    {
        var id = Text(created, "sessionId");
        if (id.Length == 0) id = Text(Property(created, "session"), "sessionId");
        Require(id.Length is > 0 and < 512, "Installed runtime did not return a bounded session identity.");
        results.Add(new("session/create", "passed"));
        var subscribed = await rpc.CallAsync("session/subscribe", new { sessionId = id, deliveryKind = "desktop-continuous", includeSnapshot = true });
        Require(subscribed.Success, "Installed runtime rejected session/subscribe."); results.Add(new("session/subscribe", "passed"));
        var read = await rpc.CallAsync("session/read", new { sessionId = id, messageLimit = 1 });
        Require(read.Success, "Installed runtime rejected session/read."); results.Add(new("session/read", "passed"));
        var stopped = await rpc.CallAsync("session/stop", new { sessionId = id });
        Require(stopped.Success, "Installed runtime rejected session/stop."); results.Add(new("session/stop", "passed"));
    }

    private static async Task WriteOfflineProviderAsync(AiAgentInstallation agent, CancellationToken token)
    {
        // These semantic schemas were inspected in the installed public bundle,
        // not inferred from a user profile. Refuse the fallback after a vendor
        // schema change rather than guessing or weakening the sandbox.
        var releasePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(agent.Path)!, "..", "config", "provider", "zcode-builtin.json"));
        using var release = JsonDocument.Parse(await File.ReadAllTextAsync(releasePath, token));
        Require(Property(release.RootElement, "schemaVersion").GetInt32() == 1 &&
            Property(Property(release.RootElement, "config"), "providerConfigRules").ValueKind == JsonValueKind.Object &&
            Property(Property(release.RootElement, "config"), "modelConfigRules").ValueKind == JsonValueKind.Object,
            "The installed provider release schema differs from the inspected schema.");
        Require(new FileInfo(agent.Path).Length is > 0 and < 128 * 1024 * 1024, "The installed schema source exceeds its validation bound.");
        var source = await File.ReadAllTextAsync(agent.Path, token);
        foreach (var anchor in new[]
        {
            "providerOrder:m.array(m.string().min(1)).optional()", "defaultModelSelection:Ll.optional()",
            "mfr=m.object({providerRules:m.array(YXi)})", "YXi=bYt.extend({config:A7.omit({builtinModelIds:!0})",
            "personalModelIds:mYt,modelOrder:mYt", "providerModelRules:m.array(G2e),manualProviderModelRules:m.array(vYt)",
            "Ll=m.object({providerId:m.string().trim().min(1),modelId:m.string().trim().min(1)",
            "H2e=m.enum([\"standard-personal\",\"zai-family\",\"bigmodel-family\"])",
            "type:m.enum([\"api-key\",\"zhipu-coding-plan-api-key\"])",
        }) Require(source.Contains(anchor, StringComparison.Ordinal), "The installed personal provider schema differs from the inspected schema.");
        var configuration = new
        {
            schemaVersion = 1,
            config = new
            {
                providerOrder = new[] { FixtureProvider },
                providerConfigRules = new
                {
                    providerRules = new[]
                    {
                        new
                        {
                            providerId = FixtureProvider, providerName = "TermLink no-send validation", enabled = true,
                            config = new
                            {
                                group = "standard-personal",
                                access = new { type = "api-key", apiKey = "TERMLINK_OFFLINE_FIXTURE_NOT_A_REAL_KEY" },
                                api = new { type = "openai-chat-completions", baseUrl = "http://127.0.0.1:9/v1" },
                                personalModelIds = new[] { FixtureModel }, modelOrder = new[] { FixtureModel },
                            }
                        }
                    }
                },
                modelConfigRules = new
                {
                    providerModelRules = new[] { new { providerId = FixtureProvider, modelId = FixtureModel, config = new { enabled = true } } },
                    manualProviderModelRules = Array.Empty<object>(),
                },
                defaultModelSelection = new { providerId = FixtureProvider, modelId = FixtureModel },
            }
        };
        var directory = Path.Combine(agent.ConfigurationDataDirectory, ".zcode", "v2"); CreatePrivateDirectory(directory);
        var path = Path.Combine(directory, "provider_config.json");
        Require(!File.Exists(path), "Offline provider configuration would overwrite an existing file.");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration), token);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool MissingConfiguration(JsonElement error)
    {
        foreach (var value in new[] { Text(error, "code"), Text(Property(error, "data"), "code"),
            Text(error, "message"), Text(Property(error, "data"), "message") })
        {
            if (value.Contains("CONFIGURATION_ERROR", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("model selection", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("no model", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("no available model", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("default model", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("默认模型", StringComparison.Ordinal) || value.Contains("模型配置", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static async Task RequireIpNetworkDeniedAsync(CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 9), deadline.Token); }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied || error.NativeErrorCode is 1 or 13) { return; }
        throw new InvalidOperationException("The loopback IP network canary was not denied by the sandbox.");
    }

    private static AiAgentInstallation Isolate(AiAgentInstallation agent, string root, string name)
    {
        var configuration = Path.Combine(root, name, "configuration");
        var sessions = Path.Combine(root, name, "sessions");
        CreatePrivateDirectory(configuration); CreatePrivateDirectory(sessions);
        return agent with { ConfigurationDataDirectory = configuration, SessionDataDirectory = sessions };
    }

    private static void ValidateIsolation(ProcessStartInfo start, AiAgentInstallation agent)
    {
        Require(start.Environment.TryGetValue("ELECTRON_RUN_AS_NODE", out var electron) && electron == "1", "The installed runtime is not in node mode.");
        Require(start.Environment.TryGetValue("ZCODE_DATA_BASE_DIR", out var config) && config == agent.ConfigurationDataDirectory &&
            start.Environment.TryGetValue("ZCODE_PERSONAL_PROVIDER_CONFIG_FILE", out var provider) &&
            provider == Path.Combine(agent.ConfigurationDataDirectory, ".zcode", "v2", "provider_config.json"), "CLI configuration isolation was not applied.");
        Require(start.Environment.TryGetValue("ZCODE_STORAGE_DIR", out var storage) && storage == agent.SessionDataDirectory &&
            start.Environment.TryGetValue("ZCODE_SESSION_DB_PATH", out var database) && database == Path.Combine(agent.SessionDataDirectory, "cli", "db", "db.sqlite"),
            "CLI session database isolation was not applied.");
    }

    private static bool IsSensitiveEnvironmentName(string value) =>
        value.Contains("KEY", StringComparison.OrdinalIgnoreCase) || value.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || value.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ZCODE_", StringComparison.OrdinalIgnoreCase) || value.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || value.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase);

    private static string PolicyQuote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static Process? SessionProcess(AiAgentSession session) => typeof(AiAgentSession)
        .GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) as Process;
    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static string SafeCode(string value) => new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(80).ToArray());
    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    private static string Text(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.String ? Property(value, name).GetString() ?? "" : "";
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken token)
    {
        var output = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum) throw new IOException("Validation process output exceeds its limit.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private sealed record Check(string Method, string Status, string? ErrorCode = null);
    private sealed record RpcReply(bool Success, JsonElement Value, string ErrorCode = "", bool MissingConfiguration = false);

    private sealed class RuntimeRpc : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenRegistration cancellation;
        private readonly Task errors;
        private int nextId, totalOutput;
        internal List<string> SubmittedMethods { get; } = [];

        internal RuntimeRpc(AiAgentInstallation agent, string workspace, CancellationToken token)
        {
            var start = AiAgentDiscovery.StartInfo(agent, workspace, "app-server");
            ValidateIsolation(start, agent);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            process = Process.Start(start) ?? throw new IOException("Installed app-server did not start.");
            cancellation = lifetime.Token.Register(() => Kill(process));
            errors = DrainErrorsAsync();
        }

        internal async Task<RpcReply> CallAsync(string method, object parameters)
        {
            Require(method is "runtime/capabilities" or "session/create" or "session/subscribe" or "session/read" or "session/stop", "Real runtime method is not allowlisted.");
            SubmittedMethods.Add(method);
            var id = "validation-" + ++nextId;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            await WriteAsync(new { id, method, @params = parameters }, deadline.Token);
            for (var count = 0; count < 128; count++)
            {
                var line = await ReadLineAsync(deadline.Token);
                using var message = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = message.RootElement; var interaction = Text(root, "method");
                if (Property(root, "id").ValueKind != JsonValueKind.Undefined && interaction.Length > 0)
                {
                    if (interaction == "session/requestRuntimePreferences")
                        await WriteAsync(new { id = Property(root, "id").Clone(), result = new
                        {
                            nativeSearchEnhancementsEnabled = false, memoryEnabled = false,
                            askUserQuestionAutoResolutionEnabled = false, modelContextBudgetStrategy = "preflight-v1",
                        } }, deadline.Token);
                    else if (interaction == "interaction/requestProviderRuntimeHeaders")
                        await WriteAsync(new { id = Property(root, "id").Clone(), result = new
                        {
                            headersApplied = false, errorMessage = "Offline validation never supplies desktop credentials",
                        } }, deadline.Token);
                    else
                        await WriteAsync(new { id = Property(root, "id").Clone(), error = new { code = -32601, message = "Read-only validation rejects interactions" } }, deadline.Token);
                    continue;
                }
                if (Text(root, "id") != id) continue;
                var error = Property(root, "error");
                if (error.ValueKind != JsonValueKind.Undefined)
                {
                    var code = Property(error, "code");
                    return new(false, default, code.ValueKind == JsonValueKind.Number ? code.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : SafeCode(Text(error, "code")),
                        RealZcodeValidation.MissingConfiguration(error));
                }
                var result = Property(root, "result");
                Require(result.ValueKind != JsonValueKind.Undefined, "App-server response has no result.");
                return new(true, result.Clone());
            }
            throw new IOException("App-server response notification limit exceeded.");
        }

        private async Task WriteAsync(object value, CancellationToken token)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        private async Task<string> ReadLineAsync(CancellationToken token)
        {
            var output = new StringBuilder(); var character = new char[1];
            while (await process.StandardOutput.ReadAsync(character, token) != 0)
            {
                Count(1);
                if (character[0] == '\n') { if (output.Length > 0) return output.ToString(); continue; }
                if (output.Length >= 64 * 1024) throw new IOException("App-server frame limit exceeded.");
                output.Append(character[0]);
            }
            throw new EndOfStreamException("Installed app-server exited before its response.");
        }
        private async Task DrainErrorsAsync()
        {
            var buffer = new char[2048]; int count;
            while ((count = await process.StandardError.ReadAsync(buffer, lifetime.Token)) > 0) Count(count);
        }
        private void Count(int count)
        {
            if (Interlocked.Add(ref totalOutput, count) <= MaximumOutputCharacters) return;
            lifetime.Cancel(); throw new IOException("App-server output limit exceeded.");
        }
        public async ValueTask DisposeAsync()
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { throw new IOException("Installed app-server did not exit after cleanup."); }
            finally
            {
                lifetime.Cancel();
                try { await errors; } catch (OperationCanceledException) { }
                cancellation.Dispose(); lifetime.Dispose(); process.Dispose();
            }
        }
    }
}

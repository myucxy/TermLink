using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TermLink.Platform;
using TermLink.Platform.Linux;
using TermLink.Platform.Unix;

if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
    throw new PlatformNotSupportedException("Unix adapter validation must run on Linux or macOS.");

var testRoot = Path.Combine(Path.GetTempPath(), $"termlink-unix-validation-{Environment.ProcessId}");
Directory.CreateDirectory(testRoot);
SetUnixMode(testRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
try
{
    await ValidateCommandsAsync();
    Console.WriteLine("PASS commands");
    await ValidateFilesAsync(testRoot);
    Console.WriteLine("PASS files");
    await ValidateIpcAsync(testRoot);
    Console.WriteLine("PASS ipc");
    await ValidatePtyAsync(testRoot);
    Console.WriteLine("PASS pty");
    ValidateAutostart(testRoot);
    Console.WriteLine("PASS autostart");
    ValidateSecretStoreFailsClosed();
    Console.WriteLine("PASS secrets");
    await ValidateAppImageAsync(testRoot);
    Console.WriteLine("PASS appimage");
    Console.WriteLine("Unix/Linux adapter validation passed: PTY, process groups, resize, Unicode/ANSI, UDS peer identity, files, commands, secrets, autostart, and AppImage rollback.");
}
finally
{
    Directory.Delete(testRoot, true);
}

static async Task ValidateCommandsAsync()
{
    var commands = await new PosixTerminalCommandDiscovery().DiscoverAsync(CancellationToken.None);
    Require(commands.Any(value => value.Id == "bash" && value.Executable.StartsWith("/", StringComparison.Ordinal)),
        "Bash was not discovered by absolute executable path.");
    Require(commands.Any(value => value.Id == "sh"), "POSIX sh was not discovered.");
}

static async Task ValidateFilesAsync(string testRoot)
{
    var allowed = Path.Combine(testRoot, "allowed");
    var outside = Path.Combine(testRoot, "outside");
    Directory.CreateDirectory(allowed);
    Directory.CreateDirectory(outside);
    await File.WriteAllTextAsync(Path.Combine(allowed, "你好.txt"), "content");
    Directory.CreateSymbolicLink(Path.Combine(allowed, "escape"), outside);
    var files = new PosixFileSystemOperations(new PosixPathPolicy([allowed]));
    var listing = await files.ListAsync(allowed, CancellationToken.None);
    Require(listing.Any(value => value.Name == "你好.txt" && !value.IsDirectory), "Unicode file was not listed.");
    await using var stream = await files.OpenReadAsync(Path.Combine(allowed, "你好.txt"), CancellationToken.None);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    Require(await reader.ReadToEndAsync() == "content", "File contents were not read exactly.");
    await RequireThrowsAsync<UnauthorizedAccessException>(
        () => files.ListAsync(Path.Combine(allowed, "escape"), CancellationToken.None).AsTask(),
        "A symlink escape outside the configured root was accepted.");
}

static async Task ValidateIpcAsync(string testRoot)
{
    var endpoint = Path.Combine(testRoot, "ipc", "agent.sock");
    var transport = new UnixDomainSocketTransport();
    await using var listener = await transport.ListenAsync(endpoint, CancellationToken.None);
    var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
    await using var client = await transport.ConnectAsync(endpoint, TimeSpan.FromSeconds(2), CancellationToken.None);
    await using var server = await accept;
    var message = "same-user-认证"u8.ToArray();
    await client.WriteAsync(message);
    var received = new byte[message.Length];
    await server.ReadExactlyAsync(received);
    Require(received.SequenceEqual(message), "Unix socket bytes were changed.");
    var mode = GetUnixMode(endpoint);
    Require(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Unix socket mode is not 0600.");
}

static async Task ValidatePtyAsync(string testRoot)
{
    await using var host = new PosixTerminalSessionHost();
    var launched = await host.LaunchAsync(new TerminalLaunchRequest(
        "bash",
        testRoot,
        80,
        24,
        new Dictionary<string, string> { ["TERMLINK_VALIDATION"] = "终端✅" }), CancellationToken.None);
    await host.SendInputAsync(launched.SessionId,
        Encoding.UTF8.GetBytes("printf '\\033[31m%s\\033[0m\\n' \"$TERMLINK_VALIDATION\"; stty size\n"),
        CancellationToken.None);
    var first = await ReadUntilAsync(host, launched.SessionId, "24 80", TimeSpan.FromSeconds(5));
    Require(first.Contains("\u001b[31m终端✅\u001b[0m", StringComparison.Ordinal), "Unicode/ANSI PTY output was not preserved.");
    await host.ResizeAsync(launched.SessionId, 132, 43, CancellationToken.None);
    await host.SendInputAsync(launched.SessionId, "stty size\n"u8.ToArray(), CancellationToken.None);
    var resized = await ReadUntilAsync(host, launched.SessionId, "43 132", TimeSpan.FromSeconds(5));
    Require(resized.Contains("43 132", StringComparison.Ordinal), "PTY resize was not visible to the shell.");
    await host.SendInputAsync(launched.SessionId, "exec sleep 30\n"u8.ToArray(), CancellationToken.None);
    _ = await ReadUntilAsync(host, launched.SessionId, "exec sleep 30", TimeSpan.FromSeconds(3));
    await Task.Delay(250);
    await host.SignalAsync(launched.SessionId, TerminalSignal.Terminate, CancellationToken.None);
    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var code = await host.WaitForExitAsync(launched.SessionId, exitTimeout.Token);
    Require(code is 143 or 129, $"The process group did not terminate as expected: {code}.");
    await host.StopAsync(launched.SessionId, CancellationToken.None);
}

static void ValidateAutostart(string testRoot)
{
    var executable = Path.Combine(testRoot, "TermLink Agent");
    var options = new LinuxAutostartOptions(executable, ["--agent", "--name=测试"], Path.Combine(testRoot, "config"));
    var unit = LinuxAutostartManager.BuildSystemdUnit(options);
    Require(unit.Contains("Restart=on-failure", StringComparison.Ordinal) &&
        unit.Contains("NoNewPrivileges=true", StringComparison.Ordinal) &&
        unit.Contains("ProtectSystem=strict", StringComparison.Ordinal),
        "The systemd hardening/restart policy is incomplete.");
    Require(unit.Contains("\"" + executable + "\"", StringComparison.Ordinal), "The systemd executable was not safely quoted.");
    var desktop = LinuxAutostartManager.BuildDesktopEntry(options);
    Require(desktop.Contains("X-GNOME-Autostart-enabled=true", StringComparison.Ordinal), "The XDG fallback is incomplete.");
}

static void ValidateSecretStoreFailsClosed()
{
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var previousBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
    try
    {
        Environment.SetEnvironmentVariable("PATH", "/no-tools");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", null);
        var store = new LinuxSecretStore(new LinuxSecretStoreOptions("validation", "kdewallet", "TermLink"));
        Require(!store.IsAvailable && store.Backend == LinuxSecretBackend.Unavailable,
            "The Linux secret store did not fail closed without a desktop secret service.");
        RequireThrows<PlatformNotSupportedException>(
            () => store.RetrieveAsync("identity", CancellationToken.None).AsTask().GetAwaiter().GetResult(),
            "The Linux secret store used an insecure fallback.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousBus);
    }
}

static async Task ValidateAppImageAsync(string testRoot)
{
    var active = Path.Combine(testRoot, "TermLink.AppImage");
    var candidate = Path.Combine(testRoot, "candidate.AppImage");
    await File.WriteAllTextAsync(active, "#!/bin/sh\nprintf old >> \"$0.log\"\nsleep 5\n");
    await File.WriteAllTextAsync(candidate, "#!/bin/sh\nprintf new >> \"$0.log\"\nsleep 5\n");
    SetUnixMode(active, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var length = new FileInfo(candidate).Length;
    await using var content = File.OpenRead(candidate);
    var sha = Convert.ToHexString(await SHA256.HashDataAsync(content)).ToLowerInvariant();
    var package = new UpdatePackage("1.2.3", 45, new Uri("https://example.invalid/TermLink.AppImage"),
        length, sha, new string('A', 40), "appimage");
    var installer = new LinuxAppImageUpdateInstaller(
        new LinuxAppImageUpdateOptions(active, Path.Combine(testRoot, "updates"), []),
        new AcceptingSignatureVerifier());
    await installer.VerifyAndStageAsync(package, candidate, CancellationToken.None);
    await installer.LaunchAsync(package, candidate, CancellationToken.None);
    await WaitForFileAsync(active + ".log", TimeSpan.FromSeconds(3));
    Require(await File.ReadAllTextAsync(active) == await File.ReadAllTextAsync(candidate), "The AppImage was not installed atomically.");
    await installer.RollbackAsync(CancellationToken.None);
    await Task.Delay(150);
    Require((await File.ReadAllTextAsync(active)).Contains("old", StringComparison.Ordinal), "The previous AppImage was not restored.");
}

static async Task<string> ReadUntilAsync(
    ITerminalSessionHost host,
    Guid sessionId,
    string marker,
    TimeSpan timeout)
{
    using var source = new CancellationTokenSource(timeout);
    var result = new StringBuilder();
    var buffer = new byte[4096];
    while (!result.ToString().Contains(marker, StringComparison.Ordinal))
    {
        var count = await host.ReadOutputAsync(sessionId, buffer, source.Token);
        if (count == 0) break;
        result.Append(Encoding.UTF8.GetString(buffer, 0, count));
    }
    return result.ToString();
}

static async Task WaitForFileAsync(string path, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(40);
    Require(File.Exists(path), $"Expected process output was not created: {path}");
}

static UnixFileMode GetUnixMode(string path)
{
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return File.GetUnixFileMode(path);
    throw new PlatformNotSupportedException();
}

static void SetUnixMode(string path, UnixFileMode mode)
{
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) File.SetUnixFileMode(path, mode);
    else throw new PlatformNotSupportedException();
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

static async Task RequireThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

file sealed class AcceptingSignatureVerifier : IArtifactSignatureVerifier
{
    public ValueTask VerifyAsync(string artifactPath, string expectedIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(File.Exists(artifactPath) && expectedIdentity.Length == 40, "Signature verification input is incomplete.");
        return ValueTask.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CryptographicException(message);
    }
}
